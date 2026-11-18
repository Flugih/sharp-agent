namespace SharpAgent.Core;

internal sealed record VpnProfile(string Name, JsonObject Outbound)
{
    internal string Label => $"{Name} · {Str(Outbound["type"])}";

    internal string Save() => Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject
    {
        ["name"] = Name, ["outbound"] = Outbound.DeepClone(),
    }.ToJsonString()));

    internal static VpnProfile? Restore(string saved)
    {
        try
        {
            var json = JsonNode.Parse(VpnSubscription.Decode(saved));
            return json?["outbound"] is JsonObject node && VpnSubscription.Profile(node, Str(json["name"])) is { } profile &&
                   !VpnSubscription.IsPlaceholder(profile) ? profile : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException) { return null; }
    }

    internal string Config(int port, string password)
    {
        var outbound = (JsonObject)Outbound.DeepClone();
        outbound["tag"] = "vpn";
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error" },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "socks", ["listen"] = "127.0.0.1", ["listen_port"] = port,
                ["users"] = new JsonArray(new JsonObject { ["username"] = "sharp-agent", ["password"] = password }),
            }),
            ["outbounds"] = new JsonArray(outbound),
            ["dns"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "bootstrap" }) },
            ["route"] = new JsonObject { ["final"] = "vpn", ["default_domain_resolver"] = "bootstrap" },
        }.ToJsonString();
    }
}

internal static class VpnSubscription
{
    const int MaxBytes = 10 * 1024 * 1024;
    const string AccessRejected = "Провайдер подписки не выдал серверы для этого устройства. Проверьте доступ и лимит устройств в кабинете провайдера.";
    static readonly string[] UserAgents = ["Karing", "Happ/3.3.6", "v2rayNG/2.2.6", "sing-box/1.13.14"];
    static readonly string[] Types = ["vless", "vmess", "trojan", "shadowsocks", "hysteria2", "hysteria", "tuic", "socks", "http", "anytls"];

    sealed class SubscriptionAccessException() : InvalidOperationException(AccessRejected);

    internal static async Task<(List<VpnProfile> Profiles, int Skipped)> LoadAsync(string source, Guid deviceId, CancellationToken ct)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0)
            throw new InvalidOperationException("Введите HTTP(S)-ссылку подписки.");
        using var client = AppNetwork.Client(cookies: false, decompression: DecompressionMethods.All);
        string? error = null;
        foreach (var userAgent in UserAgents)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(userAgent);
            request.Headers.CacheControl = new() { NoCache = true };
            request.Headers.Add("X-Hwid", deviceId.ToString("D"));
            request.Headers.Add("X-Device-Os", "Windows");
            request.Headers.Add("X-Ver-Os", Environment.OSVersion.Version.ToString(3));
            request.Headers.Add("X-Device-Model", "PC");
            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.Headers.TryGetValues("X-Hwid-Not-Supported", out var flags) &&
                    flags.Any(flag => string.Equals(flag.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
                {
                    error = AccessRejected;
                    break;
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    error = $"Сервер подписки вернул HTTP {(int)response.StatusCode}.";
                    break;
                }
                if (!response.IsSuccessStatusCode)
                {
                    error ??= $"Сервер подписки вернул HTTP {(int)response.StatusCode}.";
                    continue;
                }
                var body = await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return Parse(body);
            }
            catch (SubscriptionAccessException ex)
            {
                error = ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                error ??= ex.Message;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                error ??= "Не удалось загрузить подписку через текущее подключение.";
                break;
            }
        }
        throw new InvalidOperationException(error ?? "Пустая подписка.");
    }

    static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException("Подписка превышает 10 МБ.");
        using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (await input.ReadAsync(buffer, ct) is > 0 and var count)
        {
            if (output.Length + count > MaxBytes) throw new InvalidOperationException("Подписка превышает 10 МБ.");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    internal static (List<VpnProfile> Profiles, int Skipped) Parse(string source)
    {
        var input = source.Trim().TrimStart('\uFEFF').Trim();
        if (!input.StartsWith('{') && !input.StartsWith('[') && !input.Contains("://", StringComparison.Ordinal))
            try { input = Decode(input).Trim().TrimStart('\uFEFF').Trim(); }
            catch (FormatException) { }
        List<VpnProfile> results = [];
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        int skipped = 0;
        bool placeholder = false;
        if (input.StartsWith('{') || input.StartsWith('['))
        {
            JsonNode? json;
            try { json = JsonNode.Parse(input); }
            catch (JsonException) { throw new InvalidOperationException("Сервер вернул повреждённый JSON подписки."); }
            var nodes = json is JsonObject root ? root["outbounds"] as JsonArray : json as JsonArray;
            foreach (var node in nodes ?? [])
            {
                if (node is JsonObject entry && Str(entry["type"]) is "selector" or "urltest" or "direct" or "block" or "dns") continue;
                Add(node is JsonObject candidate ? Profile(candidate, Str(candidate["tag"])) : null);
            }
        }
        else
            foreach (var line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!line.StartsWith('#')) Add(ParseLink(line));
        if (results.Count == 0)
        {
            if (placeholder) throw new SubscriptionAccessException();
            throw new InvalidOperationException("В подписке нет поддерживаемых серверов. Поддерживаются ссылки, Base64 и JSON sing-box.");
        }
        return (results, skipped);

        void Add(VpnProfile? profile)
        {
            if (profile == null) skipped++;
            else if (IsPlaceholder(profile))
            {
                skipped++;
                placeholder = true;
            }
            else if (distinct.Add(profile.Outbound.ToJsonString())) results.Add(profile);
        }
    }

    internal static bool IsPlaceholder(VpnProfile profile) =>
        profile.Name.Contains("обновите приложение", StringComparison.OrdinalIgnoreCase) ||
        profile.Name.Contains("update app", StringComparison.OrdinalIgnoreCase) ||
        profile.Name.Contains("update the app", StringComparison.OrdinalIgnoreCase) ||
        (Str(profile.Outbound["server"])?.Trim('[', ']') is "0.0.0.0" or "::" && Long(profile.Outbound["server_port"]) == 1);

    internal static string Decode(string input)
    {
        input = string.Concat(input.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(input.PadRight((input.Length + 3) / 4 * 4, '=')));
    }

    internal static VpnProfile? Profile(JsonObject source, string? name)
    {
        var type = Str(source["type"]);
        var host = Str(source["server"]);
        if (!Types.Contains(type) || host == null || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown ||
            Long(source["server_port"]) is < 1 or > 65_535 || HasExternalReferences(source)) return null;
        if (type is "vless" or "vmess" && !Guid.TryParse(Str(source["uuid"]), out _)) return null;
        if (type is "trojan" or "shadowsocks" or "hysteria2" or "tuic" or "anytls" && Str(source["password"]) is not { Length: > 0 }) return null;
        var outbound = (JsonObject)source.DeepClone();
        outbound.Remove("tag");
        var label = string.IsNullOrWhiteSpace(name) ? host : name.ReplaceLineEndings(" ").Trim();
        return new(label[..Math.Min(label.Length, 120)], outbound);
    }

    static bool HasExternalReferences(JsonNode node) => node switch
    {
        JsonObject obj => obj.Any(item => item.Key is "detour" or "domain_resolver" or "bind_interface" or "routing_mark" or "realm" ||
            item.Key.EndsWith("_path", StringComparison.OrdinalIgnoreCase) || item.Key.EndsWith("_directory", StringComparison.OrdinalIgnoreCase) ||
            item.Value != null && HasExternalReferences(item.Value)),
        JsonArray array => array.Any(item => item != null && HasExternalReferences(item)),
        _ => false,
    };

    static VpnProfile? ParseLink(string line)
    {
        try
        {
            if (line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                var vm = JsonNode.Parse(Decode(line["vmess://".Length..])) as JsonObject;
                if (vm == null) return null;
                var outbound = Server("vmess", Str(vm["add"]), Long(vm["port"]));
                outbound["uuid"] = Str(vm["id"]);
                outbound["security"] = Str(vm["scy"]) ?? "auto";
                outbound["alter_id"] = Long(vm["aid"]);
                if (!AddTransport(outbound, Str(vm["net"]), Str(vm["host"]), Str(vm["path"]), null)) return null;
                if (Str(vm["tls"]) == "tls") AddTls(outbound, Str(vm["sni"]) ?? Str(vm["host"]), null, Str(vm["fp"]), null, null);
                return Profile(outbound, Str(vm["ps"]));
            }
            var type = line.Split(':', 2)[0].ToLowerInvariant();
            if (type == "ss") return Shadowsocks(line);
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Port < 1) return null;
            var query = Query(uri.Query);
            var kind = type switch { "hy2" => "hysteria2", "socks5" => "socks", _ => type };
            if (kind is not ("vless" or "trojan" or "hysteria2" or "socks")) return null;
            var result = Server(kind, uri.IdnHost, uri.Port);
            var user = Uri.UnescapeDataString(uri.UserInfo);
            if (kind == "socks")
            {
                var split = user.Split(':', 2);
                if (split[0].Length > 0)
                {
                    result["username"] = split[0];
                    result["password"] = split.ElementAtOrDefault(1) ?? "";
                }
                result["version"] = "5";
            }
            else result[kind == "vless" ? "uuid" : "password"] = user;
            if (kind == "vless" && query.GetValueOrDefault("flow") is { Length: > 0 } flow) result["flow"] = flow;
            if (kind is "vless" or "trojan" &&
                !AddTransport(result, query.GetValueOrDefault("type"), query.GetValueOrDefault("host"), query.GetValueOrDefault("path"), query.GetValueOrDefault("serviceName"))) return null;
            var security = query.GetValueOrDefault("security");
            if (security is not (null or "" or "none" or "tls" or "reality")) return null;
            if (security is "tls" or "reality" || kind is "trojan" or "hysteria2")
                AddTls(result, query.GetValueOrDefault("sni") ?? uri.IdnHost, query.GetValueOrDefault("alpn"), query.GetValueOrDefault("fp"),
                    security == "reality" ? query.GetValueOrDefault("pbk") : null, query.GetValueOrDefault("sid"),
                    query.GetValueOrDefault("insecure") == "1" || query.GetValueOrDefault("allowInsecure") == "1");
            if (security == "reality" && query.GetValueOrDefault("pbk") is not { Length: > 0 }) return null;
            if (kind == "hysteria2" && query.GetValueOrDefault("obfs") is { Length: > 0 } obfs)
                result["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = query.GetValueOrDefault("obfs-password") };
            return Profile(result, Uri.UnescapeDataString(uri.Fragment.TrimStart('#')));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or ArgumentException) { return null; }
    }

    static VpnProfile? Shadowsocks(string line)
    {
        var name = line.Split('#', 2);
        var parts = name[0]["ss://".Length..].Split('?', 2);
        if (parts.Length > 1 && Query(parts[1]).ContainsKey("plugin")) return null;
        var address = parts[0].TrimEnd('/');
        if (!address.Contains('@')) address = Decode(address);
        int at = address.LastIndexOf('@');
        if (at < 0 || !Uri.TryCreate("ss://" + address[(at + 1)..], UriKind.Absolute, out var uri)) return null;
        var credentials = Uri.UnescapeDataString(address[..at]);
        var pair = (credentials.Contains(':') ? credentials : Decode(credentials)).Split(':', 2);
        if (pair.Length != 2) return null;
        var outbound = Server("shadowsocks", uri.IdnHost, uri.Port);
        outbound["method"] = pair[0];
        outbound["password"] = pair[1];
        return Profile(outbound, Uri.UnescapeDataString(name.ElementAtOrDefault(1) ?? ""));
    }

    static JsonObject Server(string type, string? host, long port) => new() { ["type"] = type, ["server"] = host, ["server_port"] = port };

    static Dictionary<string, string> Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2) values[Uri.UnescapeDataString(pair[0])] = Uri.UnescapeDataString(pair[1]);
        }
        return values;
    }

    static bool AddTransport(JsonObject target, string? type, string? host, string? path, string? service)
    {
        if (type is null or "" or "tcp" or "raw") return true;
        var transport = type switch
        {
            "ws" or "httpupgrade" => new JsonObject { ["type"] = type, ["path"] = string.IsNullOrEmpty(path) ? "/" : path },
            "grpc" => new JsonObject { ["type"] = type, ["service_name"] = service ?? path ?? "" },
            "http" or "h2" => new JsonObject { ["type"] = "http", ["path"] = string.IsNullOrEmpty(path) ? "/" : path },
            _ => null,
        };
        if (transport == null) return false;
        if (host is { Length: > 0 })
        {
            if (type == "ws") transport["headers"] = new JsonObject { ["Host"] = host };
            else if (type == "httpupgrade") transport["host"] = host;
            else if (type is "http" or "h2") transport["host"] = new JsonArray(JsonValue.Create(host));
        }
        target["transport"] = transport;
        return true;
    }

    static void AddTls(JsonObject target, string? sni, string? alpn, string? fingerprint, string? publicKey, string? shortId, bool insecure = false)
    {
        var tls = new JsonObject { ["enabled"] = true, ["insecure"] = insecure };
        if (!string.IsNullOrWhiteSpace(sni)) tls["server_name"] = sni;
        if (alpn is { Length: > 0 }) tls["alpn"] = new JsonArray([.. alpn.Split(',').Select(value => JsonValue.Create(value))]);
        if (publicKey != null)
        {
            tls["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = publicKey, ["short_id"] = shortId ?? "" };
            fingerprint ??= "chrome";
        }
        if (!string.IsNullOrWhiteSpace(fingerprint))
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fingerprint };
        target["tls"] = tls;
    }
}
