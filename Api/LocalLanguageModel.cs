namespace SharpAgent.Api;

sealed record LocalLanguageModel(Uri Endpoint, string Name)
{
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2),
    }) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 1_000_000 };

    internal static LocalLanguageModel? FromSettings(string? url, string? name)
    {
        if (name is not { Length: > 0 and <= 120 } || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        if (uri.Host != "localhost" && (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))) return null;
        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/chat/completions" };
        if (uri.Host == "localhost") builder.Host = "127.0.0.1";
        return new(builder.Uri, name);
    }

    internal async Task<(string Text, UsageInfo? Usage)?> TryAsync(string input, ReplyLanguage language, CancellationToken ct)
    {
        try
        {
            var request = new JsonObject
            {
                ["model"] = Name, ["stream"] = false, ["max_tokens"] = 900,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = LanguageDetector.Guidance(language) },
                    new JsonObject { ["role"] = "user", ["content"] = input }),
            };
            using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(Endpoint, content, ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = ParseObject(await response.Content.ReadAsStringAsync(ct));
            if (json?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice ||
                Str(choice["finish_reason"]) is "length" or "content_filter") return null;
            return Str((choice["message"] as JsonObject)?["content"])?.Trim() is { Length: > 0 and <= 8000 } text
                ? (text, ChatCompletionStream.Usage(json)) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }
}
