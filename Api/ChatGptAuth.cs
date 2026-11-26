using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Serialization;

namespace SharpAgent.Api;

public sealed class ChatGptAuth(string path)
{
    internal static readonly string UserAgent = "SharpAgent/" + typeof(ChatGptAuth).Assembly.GetName().Version?.ToString(3);
    const string Issuer = "https://auth.openai.com", NewClient = "dynamic_agent_client", PlanScope = "chatgpt.tokens.use.direct",
        Resource = "https://api.openai.com/v1", Callback = "/auth/callback";
    static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5), Skew = TimeSpan.FromSeconds(5);
    static readonly string[] DeadRefresh =
        ["invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired", "refresh_token_invalidated", "refresh_token_reused"];
    static readonly HttpClient Http = AppNetwork.Client(TimeSpan.FromSeconds(30), headers: headers => headers.UserAgent.ParseAdd(UserAgent));
    static readonly JsonSerializerOptions FileFormat = new(JsonUtil.Pretty)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, IncludeFields = true,
    };
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly string _path = path;
    Credentials _record = Read(path);
    bool _unsaved;
    JsonObject? _discovery;
    JsonArray? _keys;

    sealed record Attempt(string State, string Nonce, string Verifier, string Redirect, string? ClientId, Credentials? Account, bool SelectAccount);

    sealed class Credentials
    {
        public string? Email, Label, Issuer, Subject, ClientId, PendingClientId, IdToken, AccessToken, RefreshToken, TokenType;
        public DateTimeOffset? ExpiresAt;
        public string[]? Scopes;
        public DateTimeOffset? SavedAt;
        public Dictionary<string, string>? Hosts;
        public bool PlanWelcomed, RegistrationExpired;
        public List<Credentials>? Accounts;

        public Credentials Copy() => (Credentials)MemberwiseClone();
    }

    public bool SignedIn => _record is { ClientId: not null, AccessToken: not null, RefreshToken: not null };
    public bool Connected => _record is { ClientId: not null, SavedAt: not null };
    public bool PlanAllowed => SignedIn && Granted(_record);
    public string? Label => LabelOf(_record);
    public string? ClientId => _record.ClientId;
    public IEnumerable<(string ClientId, string? Label)> SavedAccounts => Saved().Select(account => (account.ClientId!, LabelOf(account)));

    public async Task<bool> SignInAsync(Action<string> openBrowser, string? clientId, CancellationToken ct)
    {
        var (host, account, registration, selectAccount) = await Locked(() =>
        {
            var host = HostId();
            var account = clientId == null ? null : Saved().FirstOrDefault(saved => saved.ClientId == clientId);
            var registration = account is { RegistrationExpired: false } ? account.ClientId : _record.PendingClientId;
            return Task.FromResult((host, account, registration, account == null && Saved().Any()));
        }, ct);
        var listener = Listen();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{Callback}";
            var attempt = new Attempt(Secret(32), Secret(32), Secret(64), redirect, registration, account, selectAccount);
            openBrowser(AuthorizeUrl(attempt, host));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                if (await AnswerAsync(client.GetStream(), attempt, timeout.Token) is { } welcome) return welcome;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw TryAgain("Вход не завершён за 10 минут."); }
        finally { listener.Stop(); }
    }

    public Task<string> AccessAsync(CancellationToken ct) => Locked(async () =>
    {
        if (_unsaved) Save();
        if (!SignedIn) throw SignInNeeded();
        if (!(_record.ExpiresAt - DateTimeOffset.UtcNow > RefreshWindow))
        {
            try { await RefreshAsync(null); }
            catch (ApiException ex) when (ex.Retryable && SignedIn && _record.ExpiresAt - DateTimeOffset.UtcNow > Skew) { }
        }
        return _record.AccessToken!;
    }, ct);

    public Task RecoverAsync(string rejected, CancellationToken ct) => Locked(async () =>
    {
        if (!SignedIn) throw SignInNeeded();
        if (_record.AccessToken == rejected) await RefreshAsync(rejected);
        return true;
    }, ct);

    public Task RenewAsync() => Locked(async () =>
    {
        try
        {
            if (PlanAllowed && _record.SavedAt < DateTimeOffset.UtcNow - TimeSpan.FromDays(7)) await RefreshAsync(null);
        }
        catch (ApiException) { }
        return true;
    });

    public Task<bool> SignOutAsync() => Locked(async () =>
    {
        AdoptSaved();
        bool revoked = _record.RefreshToken == null || _record.ClientId == null || await RevokeAsync();
        var record = _record.Copy();
        Drop(record, keepHint: false);
        record.Accounts = Remembered(record.ClientId);
        return Commit(record) ? revoked : throw new ApiException(
            "Не удалось стереть вход из файла auth\\chatgpt.json: его держит другая программа или он только для чтения. Закройте эту программу или снимите «Только чтение» и нажмите «Выйти» ещё раз.");
    });

    async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await action(); }
        finally { _gate.Release(); }
    }

    bool Commit(Credentials record)
    {
        var previous = _record;
        _record = record;
        if (Save()) return true;
        _record = previous;
        return false;
    }

    static ApiException TryAgain(string text) => new(text + " Нажмите «Продолжить с ChatGPT» ещё раз.");

    static ApiException SignInNeeded() =>
        new("Вход в ChatGPT не выполнен или истёк. Откройте настройки и нажмите «Продолжить с ChatGPT».", HttpStatusCode.Unauthorized);

    static ApiException NotSaved() =>
        new("Сохранить вход в папку программы не удалось. Перенесите папку Sharp Agent туда, где есть права на запись, и войдите снова.");

    static ApiException Unverified() => TryAgain("OpenAI вернул вход, который не прошёл проверку подлинности.");

    static bool Granted(Credentials record) => record.Scopes?.Contains(PlanScope) == true;

    static string? LabelOf(Credentials record) => record.Label ?? record.Email;

    IEnumerable<Credentials> Saved() => (_record.Accounts ?? []).Prepend(_record)
        .Where(account => !string.IsNullOrEmpty(account?.ClientId)).DistinctBy(account => account.ClientId);

    string HostId()
    {
        var machine = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"SharpAgent|{Environment.MachineName}|{Environment.UserName}".ToUpperInvariant())))[..32].ToLowerInvariant();
        var hosts = _record.Hosts ??= new();
        if (hosts.TryGetValue(machine, out var id)) return id;
        hosts[machine] = id = "urn:uuid:" + Guid.NewGuid();
        if (Save()) return id;
        hosts.Remove(machine);
        throw NotSaved();
    }

    static string AuthorizeUrl(Attempt attempt, string host)
    {
        var query = new List<(string Name, string Value)> { ("response_type", "code"), ("client_id", attempt.ClientId ?? NewClient) };
        if (attempt.ClientId == null) query.Add(("agent_name_hint", "Sharp Agent"));
        query.AddRange(
        [
            ("ext_agent_host_id", host), ("redirect_uri", attempt.Redirect), ("scope", "openid profile email offline_access resource.invoke " + PlanScope),
            ("resource", Resource), ("state", attempt.State), ("nonce", attempt.Nonce),
            ("code_challenge", Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.Verifier)))), ("code_challenge_method", "S256"),
        ]);
        if (attempt.SelectAccount) query.Add(("prompt", "select_account"));
        if (attempt.Account is { } account)
        {
            if (account.IdToken != null) query.Add(("id_token_hint", account.IdToken));
            if (account.Email != null) query.Add(("login_hint", account.Email));
            if (account.Scopes != null && !Granted(account)) query.Add(("prompt", "consent"));
        }
        return Issuer + "/api/accounts/authorize?" + string.Join("&", query.Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value)));
    }

    static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 1455);
        try { listener.Start(); }
        catch (SocketException)
        {
            listener.Stop();
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }
        return listener;
    }

    async Task<bool?> AnswerAsync(NetworkStream stream, Attempt attempt, CancellationToken ct)
    {
        if (await ReadTargetAsync(stream, ct) is not { } target) return null;
        var parts = target.Split('?', 2);
        if (parts[0] != Callback)
        {
            await RespondAsync(stream, 404, "Не найдено", "Эта страница не относится ко входу в Sharp Agent.");
            return null;
        }
        var query = Query(parts.Length > 1 ? parts[1] : "");
        if (query.GetValueOrDefault("state") != attempt.State)
        {
            await RespondAsync(stream, 400, "Ссылка входа устарела", "Вернитесь в Sharp Agent и нажмите «Продолжить с ChatGPT» ещё раз.");
            return null;
        }
        try
        {
            var welcome = await FinishAsync(query, attempt, ct);
            await RespondAsync(stream, 200, "Вход выполнен", "Можно закрыть эту вкладку и вернуться в Sharp Agent.");
            return welcome;
        }
        catch (ApiException ex)
        {
            await RespondAsync(stream, 200, "Вход не выполнен", ex.Message);
            throw;
        }
    }

    async Task<bool> FinishAsync(Dictionary<string, string> query, Attempt attempt, CancellationToken ct)
    {
        if (query.GetValueOrDefault("error") is { Length: > 0 } error)
        {
            if (error == "invalid_client") await ExpireRegistrationAsync(attempt.ClientId, ct);
            throw error == "access_denied"
                ? new ApiException("Доступ не разрешён в браузере. Чтобы подключить ChatGPT, нажмите «Продолжить с ChatGPT» и разрешите доступ.")
                : TryAgain($"OpenAI не разрешил вход ({query.GetValueOrDefault("error_description") ?? error}).");
        }
        var returned = query.GetValueOrDefault("client_id");
        if (attempt.ClientId == null && (string.IsNullOrEmpty(returned) || returned == NewClient))
            throw TryAgain("Регистрация Sharp Agent в ChatGPT не завершилась.");
        if (attempt.ClientId != null && !string.IsNullOrEmpty(returned) && returned != attempt.ClientId)
            throw TryAgain("OpenAI вернул вход для другой регистрации программы.");
        var clientId = attempt.ClientId ?? returned!;
        if (attempt.ClientId == null)
            await Locked(() =>
            {
                _record.PendingClientId = clientId;
                return Task.FromResult(Save());
            }, ct);
        if (query.GetValueOrDefault("code") is not { Length: > 0 } code) throw TryAgain("OpenAI не передал код входа.");
        var (status, reply) = await PostAsync(Issuer + "/api/accounts/oauth/token",
        [
            new("grant_type", "authorization_code"), new("client_id", clientId), new("code", code), new("code_verifier", attempt.Verifier),
            new("redirect_uri", attempt.Redirect), new("resource", Resource),
        ], ct);
        if (!Ok(status))
        {
            var errorCode = ErrorCode(reply);
            if (errorCode == "invalid_client") await ExpireRegistrationAsync(clientId, ct);
            throw TryAgain(errorCode == "invalid_grant" ? "Код входа устарел или уже использован." : $"OpenAI не выдал вход ({Describe(status, reply)}).");
        }
        bool plan = Text(reply?["scope"])?.Split(' ').Contains(PlanScope) == true;
        if (Text(reply?["id_token"]) is not { Length: > 0 } idToken ||
            plan && (Text(reply?["access_token"]) is not { Length: > 0 } || Text(reply?["refresh_token"]) is not { Length: > 0 }))
            throw new ApiException("OpenAI выдал вход не полностью. Нажмите «Продолжить с ChatGPT» ещё раз и разрешите все запрошенные права.");
        var claims = await VerifyAsync(idToken, clientId, attempt.Nonce, ct);
        return await Locked(() =>
        {
            var subject = Text(claims["sub"]);
            if (attempt.Account is { Subject: { } expected } account && expected != subject)
                throw new ApiException($"Вы вошли в другой аккаунт ChatGPT, а выбран {LabelOf(account) ?? "прежний"}. Войдите в выбранный аккаунт или смените его в списке Sharp Agent.");
            var email = Text(claims["email"]) ?? attempt.Account?.Email;
            var record = new Credentials
            {
                Email = email, Label = attempt.Account is { } selected ? selected.Label : LabelFor(clientId, email),
                Issuer = Issuer, Subject = subject, ClientId = clientId, IdToken = idToken,
                Hosts = _record.Hosts, PlanWelcomed = _record.PlanWelcomed, Accounts = Remembered(clientId, attempt.Account?.ClientId),
            };
            Apply(record, reply!);
            bool welcome = Granted(record) && !record.PlanWelcomed;
            record.PlanWelcomed |= welcome;
            return Commit(record) ? Task.FromResult(welcome) : throw NotSaved();
        }, ct);
    }

    string? LabelFor(string clientId, string? email)
    {
        var registrations = Saved().ToList();
        if (registrations.FirstOrDefault(saved => saved.ClientId == clientId) is { } same) return same.Label;
        if (email == null || registrations.All(saved => saved.Email != email)) return null;
        int number = 2;
        while (registrations.Any(saved => saved.Label == $"{email} ({number})")) number++;
        return $"{email} ({number})";
    }

    // Keep only the metadata needed to recognize an account and sign in again.
    List<Credentials>? Remembered(string? active, string? replaced = null)
    {
        var accounts = Saved().Where(saved => saved.ClientId != active && saved.ClientId != replaced)
            .Select(saved => new Credentials
            {
                Email = saved.Email, Label = saved.Label, Subject = saved.Subject, ClientId = saved.ClientId,
                Scopes = saved.Scopes, RegistrationExpired = saved.RegistrationExpired,
            }).ToList();
        return accounts.Count > 0 ? accounts : null;
    }

    async Task<JsonObject> VerifyAsync(string jwt, string clientId, string nonce, CancellationToken ct)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3 || Decode(parts[0]) is not { } header || Decode(parts[1]) is not { } claims || Text(header["alg"]) != "RS256")
            throw Unverified();
        var kid = Text(header["kid"]);
        var keys = Matching(await KeysAsync(false, ct), kid);
        if (keys.Count == 0) keys = Matching(await KeysAsync(true, ct), kid);
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (Bytes(parts[2]) is not { } signature || !keys.Any(key => Verifies(key, signed, signature))) throw Unverified();
        bool audience = claims["aud"] is JsonArray list ? list.Any(item => Text(item) == clientId) : Text(claims["aud"]) == clientId;
        bool fresh = claims["exp"] is JsonValue exp && exp.TryGetValue<double>(out var expires) &&
            expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Skew.TotalSeconds;
        bool session = !claims.TryGetPropertyValue("sid", out var sid) || !string.IsNullOrWhiteSpace(Text(sid));
        if (Text(claims["iss"]) != Issuer || !audience || !fresh || Text(claims["nonce"]) != nonce || string.IsNullOrEmpty(Text(claims["sub"])) || !session)
            throw Unverified();
        return claims;
    }

    static List<JsonObject> Matching(JsonArray keys, string? kid) =>
        keys.OfType<JsonObject>().Where(key => Text(key["kty"]) == "RSA" && (kid == null || Text(key["kid"]) == kid) &&
            (Text(key["use"]) is null or "sig") && (Text(key["alg"]) is null or "RS256")).ToList();

    static bool Verifies(JsonObject key, byte[] data, byte[] signature)
    {
        if (Bytes(Text(key["n"])) is not { } modulus || Bytes(Text(key["e"])) is not { } exponent) return false;
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException) { return false; }
    }

    async Task<JsonObject> DiscoveryAsync(CancellationToken ct)
    {
        if (_discovery != null) return _discovery;
        var document = await GetJsonAsync(Issuer + "/.well-known/openid-configuration", ct);
        return Text(document["issuer"]) == Issuer ? _discovery = document
            : throw new ApiException("Сервер входа OpenAI ответил неожиданно. Повторите вход чуть позже.");
    }

    async Task<JsonArray> KeysAsync(bool fresh, CancellationToken ct)
    {
        if (_keys != null && !fresh) return _keys;
        var uri = Text((await DiscoveryAsync(ct))["jwks_uri"]) ?? throw Unverified();
        return _keys = (await GetJsonAsync(uri, ct))["keys"] as JsonArray ?? throw Unverified();
    }

    Task<bool> ExpireRegistrationAsync(string? clientId, CancellationToken ct) => Locked(() =>
    {
        if (clientId != null)
        {
            ExpireRegistration(clientId);
            Save();
        }
        return Task.FromResult(true);
    }, ct);

    void ExpireRegistration(string clientId)
    {
        if (_record.PendingClientId == clientId) _record.PendingClientId = null;
        if (Saved().FirstOrDefault(account => account.ClientId == clientId) is not { } account) return;
        Drop(account, keepHint: false);
        account.RegistrationExpired = true;
    }

    bool AdoptSaved()
    {
        var saved = Read(_path);
        bool newer = saved.ClientId == _record.ClientId && saved.RefreshToken != null && saved.RefreshToken != _record.RefreshToken &&
            saved.SavedAt > _record.SavedAt;
        if (newer) _record = saved;
        return newer;
    }

    async Task RefreshAsync(string? rejected)
    {
        if (AdoptSaved() && _record.AccessToken != rejected && _record.ExpiresAt - DateTimeOffset.UtcNow > RefreshWindow) return;
        var (status, reply) = await PostAsync(Issuer + "/api/accounts/oauth/token",
        [
            new("grant_type", "refresh_token"), new("client_id", _record.ClientId!), new("refresh_token", _record.RefreshToken!), new("resource", Resource),
        ], CancellationToken.None);
        if (Ok(status) && Text(reply?["access_token"]) is { Length: > 0 })
        {
            Apply(_record, reply!);
            Save();
            return;
        }
        var code = ErrorCode(reply);
        if (code == "invalid_client") ExpireRegistration(_record.ClientId!);
        else if (code is { } known && DeadRefresh.Contains(known)) Drop(_record, keepHint: true);
        else if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests)
            throw new ApiException($"Сервер входа OpenAI временно недоступен ({Describe(status, reply)}). Повторите чуть позже.", status, retryable: true);
        else
            throw new ApiException($"Не удалось продлить вход в ChatGPT ({Describe(status, reply)}). Повторите позже; если не поможет, выйдите из ChatGPT в настройках и войдите снова.",
                status);
        Save();
        throw SignInNeeded();
    }

    async Task<bool> RevokeAsync()
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (Text((await DiscoveryAsync(timeout.Token))["revocation_endpoint"]) is not { } endpoint) return false;
                var (status, _) = await PostAsync(endpoint,
                    [new("token", _record.RefreshToken!), new("token_type_hint", "refresh_token"), new("client_id", _record.ClientId!)], timeout.Token);
                if (status == HttpStatusCode.OK) return true;
                if ((int)status < 500) return false;
            }
            catch (Exception ex) when (ex is ApiException or OperationCanceledException) { }
            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt));
        }
        return false;
    }

    static void Apply(Credentials record, JsonObject reply)
    {
        record.AccessToken = Text(reply["access_token"]);
        if (Text(reply["refresh_token"]) is { Length: > 0 } refresh) record.RefreshToken = refresh;
        record.TokenType = Text(reply["token_type"]) ?? "Bearer";
        record.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(JsonUtil.Long(reply["expires_in"]) is > 0 and var seconds ? seconds : 3600);
        record.Scopes = Text(reply["scope"])?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? record.Scopes ?? [];
        record.SavedAt = DateTimeOffset.UtcNow;
    }

    static void Drop(Credentials record, bool keepHint)
    {
        record.AccessToken = record.RefreshToken = record.TokenType = null;
        record.ExpiresAt = record.SavedAt = null;
        if (!keepHint) record.IdToken = null;
    }

    static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[16 * 1024];
        int length = 0;
        try
        {
            for (int read = 1; read > 0 && length < buffer.Length && buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0; length += read)
                read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
        }
        catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested)) { return null; }
        var parts = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n", 2)[0].Split(' ');
        return parts.Length >= 3 && parts[1].StartsWith('/') ? parts[1] : null;
    }

    static Dictionary<string, string> Query(string text)
    {
        static string Unescape(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[Unescape(parts[0])] = parts.Length > 1 ? Unescape(parts[1]) : "";
        }
        return result;
    }

    static async Task RespondAsync(NetworkStream stream, int status, string title, string text)
    {
        var body = Encoding.UTF8.GetBytes(
            "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>Sharp Agent</title><style>body{margin:0;height:100vh;display:flex;" +
            "align-items:center;justify-content:center;background:#1c1c1e;color:#ececee;font:16px ui-sans-serif, -apple-system, BlinkMacSystemFont, \"Segoe UI\", Helvetica, Arial, sans-serif}main{max-width:440px;padding:24px;" +
            "text-align:center}h1{font-size:22px;font-weight:600;margin:0 0 12px}p{color:#c9c9ce;line-height:1.5;margin:0}</style></head><body><main>" +
            $"<h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(text)}</p></main></body></html>");
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
        }
        catch (IOException) { }
    }

    static Task<(HttpStatusCode Status, JsonObject? Body)> PostAsync(string url, KeyValuePair<string, string>[] form, CancellationToken ct) =>
        SendAsync(new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) }, ct);

    static async Task<JsonObject> GetJsonAsync(string url, CancellationToken ct)
    {
        var (status, body) = await SendAsync(new(HttpMethod.Get, url), ct);
        return Ok(status) && body != null ? body
            : throw new ApiException($"Сервер входа OpenAI ответил ошибкой ({Describe(status, body)}). Повторите вход чуть позже.", status, retryable: (int)status >= 500);
    }

    static async Task<(HttpStatusCode Status, JsonObject? Body)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using (request)
            using (var response = await Http.SendAsync(request, ct))
                return (response.StatusCode, JsonUtil.ParseObject(await response.Content.ReadAsStringAsync(ct)));
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException("Нет связи с сервером входа OpenAI. Проверьте подключение к интернету и повторите.\n" + ex.Message, retryable: true)
            { ConnectionInterrupted = ApiException.IsConnectionInterrupted(ex) };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("Сервер входа OpenAI не ответил вовремя. Повторите попытку.", retryable: true)
            { ConnectionInterrupted = true };
        }
    }

    static bool Ok(HttpStatusCode status) => (int)status is >= 200 and <= 299;

    static string? ErrorCode(JsonObject? body) => Text(body?["error"]) ?? Text((body?["error"] as JsonObject)?["code"]) ?? Text(body?["code"]);

    static string Describe(HttpStatusCode status, JsonObject? body) => $"HTTP {(int)status}" + (ErrorCode(body) is { } code ? ", код: " + code : "");

    static JsonObject? Decode(string segment)
    {
        try { return Bytes(segment) is { } bytes ? JsonNode.Parse(bytes) as JsonObject : null; }
        catch (JsonException) { return null; }
    }

    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static string Secret(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[]? Bytes(string? base64Url)
    {
        if (base64Url == null) return null;
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        try { return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=')); }
        catch (FormatException) { return null; }
    }

    static Credentials Read(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<Credentials>(AppPaths.ReadAuth(File.ReadAllText(file, Encoding.UTF8)), FileFormat) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    bool Save()
    {
        var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var file = new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, OwnerOnly()))
            {
                var json = JsonSerializer.Serialize(_record, FileFormat);
                file.Write(Encoding.UTF8.GetBytes(AppPaths.ProtectSecrets ? AppPaths.ProtectAuth(json) : json));
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            _unsaved = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _unsaved = true;
            try { File.Delete(temp); }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    static FileSecurity OwnerOnly()
    {
        using var user = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
