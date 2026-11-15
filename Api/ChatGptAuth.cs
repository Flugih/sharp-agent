using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpAgent.Core;

namespace SharpAgent.Api;

public sealed class ChatGptAuth
{
    internal static readonly string UserAgent = "SharpAgent/" + typeof(ChatGptAuth).Assembly.GetName().Version?.ToString(3);

    private const string AppName = "Sharp Agent";
    private const string NewClient = "dynamic_agent_client";
    private const string PlanScope = "chatgpt.tokens.use.direct";
    private const string Scope = "openid profile email offline_access resource.invoke " + PlanScope;
    private const string Resource = "https://api.openai.com/v1";
    private const string Callback = "/auth/callback";
    private const int PreferredPort = 1455, RevokeAttempts = 3;
    private static readonly TimeSpan SignInTime = TimeSpan.FromMinutes(10), RefreshWindow = TimeSpan.FromMinutes(5),
        Skew = TimeSpan.FromSeconds(5), RevokeTime = TimeSpan.FromSeconds(10), RenewAge = TimeSpan.FromDays(7);
    private static readonly string[] DeadRefresh =
    [
        "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired", "refresh_token_invalidated", "refresh_token_reused",
    ];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30), DefaultRequestHeaders = { { "User-Agent", UserAgent } } };
    private static readonly JsonSerializerOptions FileFormat = new(JsonUtil.Pretty)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private readonly string _issuer = "https://auth.openai.com";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private Credentials _record;
    private bool _unsaved;
    private JsonObject? _discovery;
    private JsonArray? _keys;

    private sealed record Attempt(string State, string Nonce, string Verifier, string Redirect, string? ClientId, Credentials? Account);

    private sealed class Credentials
    {
        public string? Email { get; set; }
        public string? Label { get; set; }
        public string? Issuer { get; set; }
        public string? Subject { get; set; }
        public string? ClientId { get; set; }
        public string? PendingClientId { get; set; }
        public string? IdToken { get; set; }
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public string? TokenType { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public string[]? Scopes { get; set; }
        public DateTimeOffset? SavedAt { get; set; }
        public Dictionary<string, string>? Hosts { get; set; }
        public bool PlanWelcomed { get; set; }
        public List<Credentials>? Accounts { get; set; }

        public Credentials Copy() => (Credentials)MemberwiseClone();
    }

    public ChatGptAuth(string path)
    {
        _path = path;
        _record = Read(path);
    }

    public bool SignedIn => _record is { ClientId: not null, AccessToken: not null, RefreshToken: not null };

    public bool Connected => _record is { ClientId: not null, SavedAt: not null };

    public bool PlanAllowed => SignedIn && Granted(_record);

    public string? Label => LabelOf(_record);

    public string? ClientId => _record.ClientId;

    public IEnumerable<(string ClientId, string? Label)> SavedAccounts =>
        (_record.Accounts ?? []).Select(account => (account.ClientId!, LabelOf(account)));

    public async Task<bool> SignInAsync(Action<string> openBrowser, string? clientId, CancellationToken ct)
    {
        string host;
        await _gate.WaitAsync(ct);
        try
        {
            host = HostId();
        }
        finally
        {
            _gate.Release();
        }

        var listener = Listen();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{Callback}";
            var account = clientId == null ? null : (_record.Accounts ?? []).Prepend(_record).FirstOrDefault(saved => saved.ClientId == clientId);
            var attempt = new Attempt(Secret(32), Secret(32), Secret(64), redirect, clientId ?? _record.PendingClientId, account);
            openBrowser(AuthorizeUrl(attempt, host));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SignInTime);
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                if (await AnswerAsync(client.GetStream(), attempt, timeout.Token) is { } welcome) return welcome;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("Вход не завершён за 10 минут. Нажмите «Продолжить с ChatGPT» ещё раз.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<string> AccessAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_unsaved) Save();
            if (!SignedIn) throw SignInNeeded();
            if (!(_record.ExpiresAt - DateTimeOffset.UtcNow > RefreshWindow))
            {
                try
                {
                    await RefreshAsync(null);
                }
                catch (ApiException ex) when (ex.Retryable && SignedIn && _record.ExpiresAt - DateTimeOffset.UtcNow > Skew)
                {
                }
            }
            return _record.AccessToken!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecoverAsync(string rejected, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!SignedIn) throw SignInNeeded();
            if (_record.AccessToken == rejected) await RefreshAsync(rejected);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenewAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (PlanAllowed && _record.SavedAt < DateTimeOffset.UtcNow - RenewAge) await RefreshAsync(null);
        }
        catch (ApiException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SignOutAsync()
    {
        await _gate.WaitAsync();
        try
        {
            AdoptSaved();
            bool revoked = _record.RefreshToken == null || _record.ClientId == null || await RevokeAsync();
            var previous = _record;
            _record = previous.Copy();
            Drop(_record, keepHint: false);
            if (Save()) return revoked;
            _record = previous;
            throw new ApiException("Не удалось стереть вход из файла auth\\chatgpt.json: его держит другая программа или он только " +
                                   "для чтения. Закройте эту программу или снимите «Только чтение» и нажмите «Выйти» ещё раз.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ApiException SignInNeeded() =>
        new("Вход в ChatGPT не выполнен или истёк. Откройте настройки и нажмите «Продолжить с ChatGPT».", HttpStatusCode.Unauthorized);

    private static ApiException NotSaved() =>
        new("Сохранить вход в папку программы не удалось. Перенесите папку Sharp Agent туда, где есть права на запись, и войдите снова.");

    private static ApiException Unverified() =>
        new("OpenAI вернул вход, который не прошёл проверку подлинности. Нажмите «Продолжить с ChatGPT» ещё раз.");

    private static bool Granted(Credentials record) => record.Scopes?.Contains(PlanScope) == true;

    private static string? LabelOf(Credentials record) => record.Label ?? record.Email;

    private string HostId()
    {
        var machine = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"SharpAgent|{Environment.MachineName}|{Environment.UserName}".ToUpperInvariant())))[..32].ToLowerInvariant();
        var hosts = _record.Hosts ??= new Dictionary<string, string>();
        if (hosts.TryGetValue(machine, out var id)) return id;
        hosts[machine] = id = "urn:uuid:" + Guid.NewGuid();
        if (Save()) return id;
        hosts.Remove(machine);
        throw NotSaved();
    }

    private string AuthorizeUrl(Attempt attempt, string host)
    {
        var query = new List<(string Name, string Value)> { ("response_type", "code"), ("client_id", attempt.ClientId ?? NewClient) };
        if (attempt.ClientId == null) query.Add(("agent_name_hint", AppName));
        query.AddRange(
        [
            ("ext_agent_host_id", host), ("redirect_uri", attempt.Redirect), ("scope", Scope), ("resource", Resource), ("state", attempt.State),
            ("nonce", attempt.Nonce), ("code_challenge", Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(attempt.Verifier)))),
            ("code_challenge_method", "S256"),
        ]);
        if (attempt.Account is { } account)
        {
            if (account.IdToken != null) query.Add(("id_token_hint", account.IdToken));
            if (account.Email != null) query.Add(("login_hint", account.Email));
            if (account.Scopes != null && !Granted(account)) query.Add(("prompt", "consent"));
        }
        return _issuer + "/api/accounts/authorize?" + string.Join("&", query.Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value)));
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, PreferredPort);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            listener.Stop();
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }
        return listener;
    }

    private async Task<bool?> AnswerAsync(NetworkStream stream, Attempt attempt, CancellationToken ct)
    {
        if (await ReadTargetAsync(stream, ct) is not { } target) return null;
        int mark = target.IndexOf('?');
        if ((mark < 0 ? target : target[..mark]) != Callback)
        {
            await RespondAsync(stream, 404, "Не найдено", "Эта страница не относится ко входу в Sharp Agent.");
            return null;
        }
        var query = Query(mark < 0 ? "" : target[(mark + 1)..]);
        if (query.GetValueOrDefault("state") != attempt.State)
        {
            await RespondAsync(stream, 400, "Ссылка входа устарела", "Вернитесь в Sharp Agent и нажмите «Продолжить с ChatGPT» ещё раз.");
            return null;
        }

        bool welcome;
        try
        {
            welcome = await FinishAsync(query, attempt, ct);
        }
        catch (ApiException ex)
        {
            await RespondAsync(stream, 200, "Вход не выполнен", ex.Message);
            throw;
        }
        await RespondAsync(stream, 200, "Вход выполнен", "Можно закрыть эту вкладку и вернуться в Sharp Agent.");
        return welcome;
    }

    private async Task<bool> FinishAsync(Dictionary<string, string> query, Attempt attempt, CancellationToken ct)
    {
        if (query.GetValueOrDefault("error") is { Length: > 0 } error)
            throw new ApiException(error == "access_denied"
                ? "Доступ не разрешён в браузере. Чтобы подключить ChatGPT, нажмите «Продолжить с ChatGPT» и разрешите доступ."
                : $"OpenAI не разрешил вход ({query.GetValueOrDefault("error_description") ?? error}). Нажмите «Продолжить с ChatGPT» ещё раз.");
        var returned = query.GetValueOrDefault("client_id");
        if (attempt.ClientId == null && (string.IsNullOrEmpty(returned) || returned == NewClient))
            throw new ApiException("Регистрация Sharp Agent в ChatGPT не завершилась. Нажмите «Продолжить с ChatGPT» ещё раз.");
        if (attempt.ClientId != null && !string.IsNullOrEmpty(returned) && returned != attempt.ClientId)
            throw new ApiException("OpenAI вернул вход для другой регистрации программы. Нажмите «Продолжить с ChatGPT» ещё раз.");
        var clientId = attempt.ClientId ?? returned!;
        if (attempt.ClientId == null) await KeepClientAsync(clientId, ct);
        if (query.GetValueOrDefault("code") is not { Length: > 0 } code)
            throw new ApiException("OpenAI не передал код входа. Нажмите «Продолжить с ChatGPT» ещё раз.");

        var (status, reply) = await PostAsync(_issuer + "/api/accounts/oauth/token",
        [
            new("grant_type", "authorization_code"), new("client_id", clientId), new("code", code),
            new("code_verifier", attempt.Verifier), new("redirect_uri", attempt.Redirect), new("resource", Resource),
        ], ct);
        if (!Ok(status))
            throw new ApiException(ErrorCode(reply) == "invalid_grant"
                ? "Код входа устарел или уже использован. Нажмите «Продолжить с ChatGPT» ещё раз."
                : $"OpenAI не выдал вход ({Describe(status, reply)}). Нажмите «Продолжить с ChatGPT» ещё раз.");
        bool plan = Text(reply?["scope"])?.Split(' ').Contains(PlanScope) == true;
        if (Text(reply?["id_token"]) is not { Length: > 0 } idToken ||
            plan && (Text(reply?["access_token"]) is not { Length: > 0 } || Text(reply?["refresh_token"]) is not { Length: > 0 }))
            throw new ApiException("OpenAI выдал вход не полностью. Нажмите «Продолжить с ChatGPT» ещё раз и разрешите все запрошенные права.");
        var claims = await VerifyAsync(idToken, clientId, attempt.Nonce, ct);

        await _gate.WaitAsync(ct);
        try
        {
            var subject = Text(claims["sub"]);
            if (attempt.Account is { Subject: { } expected } account && expected != subject)
                throw new ApiException($"Вы вошли в другой аккаунт ChatGPT, а выбран {LabelOf(account) ?? "прежний"}. Войдите в него — или " +
                                       (Connected ? "нажмите в настройках Sharp Agent «Выйти» и выберите другой аккаунт."
                                           : "выберите другой аккаунт в настройках Sharp Agent."));
            var email = Text(claims["email"]);
            var record = new Credentials
            {
                Email = email, Label = LabelFor(clientId, email), Issuer = _issuer, Subject = subject, ClientId = clientId,
                IdToken = idToken, Hosts = _record.Hosts, PlanWelcomed = _record.PlanWelcomed, Accounts = Remembered(clientId),
            };
            Apply(record, reply!);
            bool welcome = Granted(record) && !record.PlanWelcomed;
            record.PlanWelcomed |= welcome;
            var previous = _record;
            _record = record;
            if (Save()) return welcome;
            _record = previous;
            throw NotSaved();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? LabelFor(string clientId, string? email)
    {
        var registrations = (_record.Accounts ?? []).Prepend(_record).Where(saved => saved.ClientId != null).ToList();
        if (registrations.FirstOrDefault(saved => saved.ClientId == clientId) is { } same) return same.Label;
        if (email == null || registrations.All(saved => saved.Email != email)) return null;
        int number = 2;
        while (registrations.Any(saved => saved.Label == $"{email} ({number})")) number++;
        return $"{email} ({number})";
    }

    private List<Credentials>? Remembered(string active)
    {
        var accounts = (_record.Accounts ?? []).Where(saved => saved.ClientId != active && saved.ClientId != _record.ClientId).ToList();
        if (_record.ClientId is { } previous && previous != active)
            accounts.Insert(0, new Credentials
            {
                Email = _record.Email, Label = _record.Label, Subject = _record.Subject, ClientId = previous, Scopes = _record.Scopes,
            });
        return accounts.Count > 0 ? accounts : null;
    }

    private async Task KeepClientAsync(string clientId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _record.PendingClientId = clientId;
            Save();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonObject> VerifyAsync(string jwt, string clientId, string nonce, CancellationToken ct)
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
        if (Text(claims["iss"]) != _issuer || !audience || !fresh || Text(claims["nonce"]) != nonce ||
            string.IsNullOrEmpty(Text(claims["sub"])) || !session)
            throw Unverified();
        return claims;
    }

    private static List<JsonObject> Matching(JsonArray keys, string? kid) =>
        keys.OfType<JsonObject>().Where(key => Text(key["kty"]) == "RSA" && (kid == null || Text(key["kid"]) == kid) &&
                                               (Text(key["use"]) is null or "sig") && (Text(key["alg"]) is null or "RS256")).ToList();

    private static bool Verifies(JsonObject key, byte[] data, byte[] signature)
    {
        if (Bytes(Text(key["n"])) is not { } modulus || Bytes(Text(key["e"])) is not { } exponent) return false;
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private async Task<JsonObject> DiscoveryAsync(CancellationToken ct)
    {
        if (_discovery != null) return _discovery;
        var document = await GetJsonAsync(_issuer + "/.well-known/openid-configuration", ct);
        if (Text(document["issuer"]) != _issuer) throw new ApiException("Сервер входа OpenAI ответил неожиданно. Повторите вход чуть позже.");
        return _discovery = document;
    }

    private async Task<JsonArray> KeysAsync(bool fresh, CancellationToken ct)
    {
        if (_keys != null && !fresh) return _keys;
        var uri = Text((await DiscoveryAsync(ct))["jwks_uri"]) ?? throw Unverified();
        return _keys = (await GetJsonAsync(uri, ct))["keys"] as JsonArray ?? throw Unverified();
    }

    private bool AdoptSaved()
    {
        var saved = Read(_path);
        bool newer = saved.ClientId == _record.ClientId && saved.RefreshToken != null && saved.RefreshToken != _record.RefreshToken &&
                     saved.SavedAt > _record.SavedAt;
        if (newer) _record = saved;
        return newer;
    }

    private async Task RefreshAsync(string? rejected)
    {
        if (AdoptSaved() && _record.AccessToken != rejected && _record.ExpiresAt - DateTimeOffset.UtcNow > RefreshWindow) return;

        var (status, reply) = await PostAsync(_issuer + "/api/accounts/oauth/token",
        [
            new("grant_type", "refresh_token"), new("client_id", _record.ClientId!), new("refresh_token", _record.RefreshToken!),
            new("resource", Resource),
        ], CancellationToken.None);
        if (Ok(status) && Text(reply?["access_token"]) is { Length: > 0 })
        {
            Apply(_record, reply!);
            Save();
            return;
        }

        var code = ErrorCode(reply);
        if (code == "invalid_client" || code is { } known && DeadRefresh.Contains(known))
        {
            if (code == "invalid_client")
                _record = new Credentials { Hosts = _record.Hosts, PlanWelcomed = _record.PlanWelcomed, Accounts = _record.Accounts };
            else Drop(_record, keepHint: true);
            Save();
            throw SignInNeeded();
        }
        if ((int)status >= 500 || status == HttpStatusCode.TooManyRequests)
            throw new ApiException($"Сервер входа OpenAI временно недоступен ({Describe(status, reply)}). Повторите чуть позже.", status, retryable: true);
        throw new ApiException($"Не удалось продлить вход в ChatGPT ({Describe(status, reply)}). Повторите позже; если не поможет, " +
                               "выйдите из ChatGPT в настройках и войдите снова.", status);
    }

    private async Task<bool> RevokeAsync()
    {
        for (int attempt = 1; attempt <= RevokeAttempts; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(RevokeTime);
                if (Text((await DiscoveryAsync(timeout.Token))["revocation_endpoint"]) is not { } endpoint) return false;
                var (status, _) = await PostAsync(endpoint,
                [
                    new("token", _record.RefreshToken!), new("token_type_hint", "refresh_token"), new("client_id", _record.ClientId!),
                ], timeout.Token);
                if (status == HttpStatusCode.OK) return true;
                if ((int)status < 500) return false;
            }
            catch (Exception ex) when (ex is ApiException or OperationCanceledException)
            {
            }
            if (attempt < RevokeAttempts) await Task.Delay(TimeSpan.FromSeconds(attempt));
        }
        return false;
    }

    private static void Apply(Credentials record, JsonObject reply)
    {
        record.AccessToken = Text(reply["access_token"]);
        if (Text(reply["refresh_token"]) is { Length: > 0 } refresh) record.RefreshToken = refresh;
        record.TokenType = Text(reply["token_type"]) ?? "Bearer";
        record.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(JsonUtil.Long(reply["expires_in"]) is > 0 and var seconds ? seconds : 3600);
        record.Scopes = Text(reply["scope"])?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? record.Scopes ?? [];
        record.SavedAt = DateTimeOffset.UtcNow;
    }

    private static void Drop(Credentials record, bool keepHint)
    {
        record.AccessToken = record.RefreshToken = record.TokenType = null;
        record.ExpiresAt = record.SavedAt = null;
        if (!keepHint) record.IdToken = null;
    }

    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[16 * 1024];
        int length = 0;
        try
        {
            while (length < buffer.Length && buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
                if (read == 0) break;
                length += read;
            }
        }
        catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
        var parts = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n", 2)[0].Split(' ');
        return parts.Length >= 3 && parts[1].StartsWith('/') ? parts[1] : null;
    }

    private static Dictionary<string, string> Query(string text)
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

    private static async Task RespondAsync(NetworkStream stream, int status, string title, string text)
    {
        var body = Encoding.UTF8.GetBytes(
            "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>Sharp Agent</title><style>" +
            "body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;background:#1c1c1e;" +
            "color:#ececee;font:16px 'Segoe UI',sans-serif}main{max-width:440px;padding:24px;text-align:center}" +
            "h1{font-size:22px;font-weight:600;margin:0 0 12px}p{color:#c9c9ce;line-height:1.5;margin:0}</style></head><body><main>" +
            $"<h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(text)}</p></main></body></html>");
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                                           $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
        }
        catch (IOException)
        {
        }
    }

    private static Task<(HttpStatusCode Status, JsonObject? Body)> PostAsync(string url, KeyValuePair<string, string>[] form,
        CancellationToken ct) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) }, ct);

    private static async Task<JsonObject> GetJsonAsync(string url, CancellationToken ct)
    {
        var (status, body) = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        return Ok(status) && body != null
            ? body
            : throw new ApiException($"Сервер входа OpenAI ответил ошибкой ({Describe(status, body)}). Повторите вход чуть позже.", status,
                retryable: (int)status >= 500);
    }

    private static async Task<(HttpStatusCode Status, JsonObject? Body)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using (request)
            using (var response = await Http.SendAsync(request, ct))
                return (response.StatusCode, JsonUtil.ParseObject(await response.Content.ReadAsStringAsync(ct)));
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException("Нет связи с сервером входа OpenAI. Проверьте подключение к интернету и повторите.\n" + ex.Message,
                retryable: true);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("Сервер входа OpenAI не ответил вовремя. Повторите попытку.", retryable: true);
        }
    }

    private static bool Ok(HttpStatusCode status) => (int)status is >= 200 and <= 299;

    private static string? ErrorCode(JsonObject? body) =>
        Text(body?["error"]) ?? Text((body?["error"] as JsonObject)?["code"]) ?? Text(body?["code"]);

    private static string Describe(HttpStatusCode status, JsonObject? body) =>
        $"HTTP {(int)status}" + (ErrorCode(body) is { } code ? ", код: " + code : "");

    private static JsonObject? Decode(string segment)
    {
        try
        {
            return Bytes(segment) is { } bytes ? JsonNode.Parse(bytes) as JsonObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Secret(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? Bytes(string? base64Url)
    {
        if (base64Url == null) return null;
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        try
        {
            return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Credentials Read(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Credentials>(File.ReadAllText(file, Encoding.UTF8), FileFormat) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Credentials();
        }
    }

    private bool Save()
    {
        var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var file = new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096,
                       FileOptions.None, OwnerOnly()))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(_record, FileFormat));
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            _unsaved = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _unsaved = true;
            try
            {
                File.Delete(temp);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
            }
            return false;
        }
    }

    private static FileSecurity OwnerOnly()
    {
        using var user = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
