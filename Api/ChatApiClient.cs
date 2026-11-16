namespace SharpAgent.Api;

public sealed record UsageInfo(long PromptTokens, long CompletionTokens);
public sealed record ModelProgress(string? Reasoning = null, string? Commentary = null);
public sealed record ChatResult(JsonObject Message, string? FinishReason, UsageInfo? Usage, string? RequestId, ModelProgress? Progress = null,
    string? Phase = null);
public sealed record ModelInfo(string Id, long? ContextTokens, bool? Vision, bool Chat, bool Images, string[] Efforts, string? Name = null);
public sealed record BalanceInfo(long Balance);
public sealed record UsageEntry(string Model, bool Success, long TotalTokens, long Cost, DateTime CreatedUtc);
public sealed record UsagePage(List<UsageEntry> Entries, string? NextCursor);

public interface IChatProvider : IDisposable
{
    event Action<int, int>? Retrying;
    Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct);
    Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct);
}

public sealed class ApiException(string message, HttpStatusCode? status = null, bool retryable = false, string? code = null,
    TimeSpan? retryAfter = null) : Exception(message)
{
    internal static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    public HttpStatusCode? StatusCode { get; } = status;
    public bool Retryable { get; } = retryable;
    public string? Code { get; } = code;
    public string? Parameter { get; init; }
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static ApiException FromResponse(HttpStatusCode status, string body, string? requestId, TimeSpan? retryAfter = null)
    {
        var error = ParseObject(body)?["error"] as JsonObject;
        string? Field(string name) => Str(error?[name]);
        string? title = Field("title"), message = Field("message"), code = Field("code") ?? Field("type");
        int number = (int)status;
        var text = (title ?? $"Сервер ответил ошибкой (HTTP {number})") +
            (message != null ? ": " + message : title == null && body.Trim() is [not '<', ..] raw ? ": " + Truncate(raw, 400) : "") +
            (Field("action") is { } action ? "\n" + action : "") + (Field("billing") is { } billing ? "\n" + billing : "") +
            Tail(number, code, requestId ?? Field("request_id"));
        return new(text, status, Bool(error?["retryable"]) ?? (number == 429 || number >= 500), code, retryAfter) { Parameter = Field("param") };
    }

    internal static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    internal bool RejectsStream(bool options = false) =>
        StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented &&
        (Parameter == (options ? "stream_options" : "stream") ||
         Regex.IsMatch(Message, options ? @"\b(stream_options|include_usage)\b" : @"\bstream(?:ing)?\b", RegexOptions.IgnoreCase));

    internal static string Tail(int? status, string? code, string? requestId) =>
        string.Join(", ", new[] { status is { } number ? "HTTP " + number : null, code != null ? "код: " + code : null,
            requestId != null ? "request_id: " + requestId : null }.OfType<string>()) is { Length: > 0 } parts ? $"\n[{parts}]" : "";

    internal static async Task<ChatResult> RetryAsync(Func<Task<ChatResult>> send, Func<ApiException, bool?> fallback, Action reset,
        Action<int, int> retrying, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await send();
            }
            catch (Exception ex)
            {
                var adapted = ex is ApiException api ? fallback(api) : false;
                if (adapted == true)
                {
                    attempt--;
                    continue;
                }
                if (adapted == null || attempt >= maxAttempts || RetryDelay(ex, attempt, ct) is not { } delay) throw;
                reset();
                retrying(attempt + 1, maxAttempts);
                await Task.Delay(delay, ct);
            }
        }
    }

    static TimeSpan? RetryDelay(Exception ex, int attempt, CancellationToken ct) => ex switch
    {
        ApiException { Retryable: true, RetryAfter: { } wait } => wait <= MaxWait ? wait : null,
        ApiException { Retryable: true } => TimeSpan.FromSeconds(2 * attempt * attempt),
        HttpRequestException or IOException => TimeSpan.FromSeconds(2 * attempt),
        TaskCanceledException when !ct.IsCancellationRequested => TimeSpan.Zero,
        _ => null,
    };

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public sealed class ChatApiClient(string baseUrl, string apiKey, AgentProvider provider = AgentProvider.Tooken) : IChatProvider
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);
    static readonly string[] ContextKeys = ["safe_context_tokens", "context_window_tokens", "context_window", "context_length"], StandardEfforts = ["low", "medium", "high"];
    static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 64 << 20 };
    readonly string _baseUrl = baseUrl.TrimEnd('/');
    readonly bool _groq = provider == AgentProvider.Groq;
    readonly HttpClient _http = new()
    {
        Timeout = RequestTimeout,
        DefaultRequestHeaders = { Authorization = new("Bearer", apiKey), UserAgent = { new("SharpAgent", "1.0") }, Accept = { new("application/json") } },
    };
    readonly HashSet<string> _noStreaming = new(StringComparer.OrdinalIgnoreCase), _noStreamOptions = new(StringComparer.OrdinalIgnoreCase);

    public event Action<int, int>? Retrying;
    public event Action? LimitsChanged;
    public GroqLimits? Limits { get; private set; }

    public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model, ["messages"] = new JsonArray((_groq ? Groq.Clean(messages) : messages.Select(m => m.DeepClone())).ToArray()),
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        if (maxTokens is > 0) body["max_tokens"] = maxTokens.Value;
        if (effort != null) body["reasoning_effort"] = effort;
        bool stream = !_noStreaming.Contains(model), options = stream && !_noStreamOptions.Contains(model);
        if (options) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        void Reset()
        {
            reportText(null, false);
            reportProgress(new());
        }
        return ApiException.RetryAsync(() =>
        {
            body["stream"] = stream;
            return SendCompletionAsync(body.ToJsonString(), stream, reportText, reportProgress, ct);
        }, ex =>
        {
            if (options && ex.RejectsStream(options: true))
            {
                options = false;
                _noStreamOptions.Add(model);
            }
            else if (stream && ex.RejectsStream())
            {
                stream = options = false;
                _noStreaming.Add(model);
                Reset();
            }
            else return false;
            body.Remove("stream_options");
            return true;
        }, Reset, (attempt, total) => Retrying?.Invoke(attempt, total), ct);
    }

    async Task<ChatResult> SendCompletionAsync(string json, bool stream, Action<string?, bool> reportText, Action<ModelProgress> reportProgress,
        CancellationToken ct)
    {
        using var request = PostRequest("/chat/completions", json);
        if (stream) request.Headers.Add("Accept", "application/json, text/event-stream");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var requestId = ApiException.Header(response, "X-Request-ID");
            if (response.IsSuccessStatusCode &&
                string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                Check(response, "", requestId);
                return await ChatCompletionStream.ReadAsync(response, requestId, reportText, reportProgress, timeout.Token);
            }
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            Check(response, text, requestId);
            JsonObject? root;
            try { root = JsonNode.Parse(text) as JsonObject; }
            catch (JsonException) { throw new ApiException("Сервер вернул не JSON: " + ApiException.Truncate(text, 400)); }
            if (root?["error"] is JsonObject) throw ApiException.FromResponse(response.StatusCode, text, requestId);
            return (root?["choices"] as JsonArray)?.FirstOrDefault() is JsonObject choice && choice["message"] is JsonObject message
                ? new((JsonObject)message.DeepClone(), Str(choice["finish_reason"]), ChatCompletionStream.Usage(root), requestId)
                : throw new ApiException("Неожиданный ответ сервера (нет choices[0].message): " + ApiException.Truncate(text, 400));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("Модель не ответила за отведённое время. Нажмите «Повторить».", retryable: true);
        }
    }

    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        List<ModelInfo> result = [];
        if ((await GetJsonAsync("/models", ct))["data"] is not JsonArray data) return result;
        foreach (var item in data)
        {
            var obj = item as JsonObject;
            var id = Str(obj != null ? obj["id"] : item);
            if (string.IsNullOrWhiteSpace(id) || result.Any(m => m.Id == id) || _groq && !Groq.IsChatModel(id, obj)) continue;
            var caps = obj?["capabilities"] as JsonObject;
            var endpoints = (caps?["endpoints"] as JsonArray)?.Select(Str).ToList();
            bool images = endpoints?.Contains("images") == true;
            long context = ContextKeys.Select(key => Long(obj?[key])).FirstOrDefault(tokens => tokens > 0);
            result.Add(new(id, context > 0 ? context : null, Bool(caps?["vision"]), (endpoints?.Contains("chat") ?? !images) && Bool(caps?["tools"]) != false,
                images, _groq ? Groq.EffortsOf(id) : Bool(caps?["reasoning_effort"]) == true &&
                    (caps?["reasoning_endpoints"] as JsonArray)?.Select(Str).Contains("chat") == true ? StandardEfforts : []));
        }
        result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public async Task<byte[]> GenerateImageAsync(string model, string prompt, string size, CancellationToken ct)
    {
        using var request = PostRequest("/images/generations", new JsonObject { ["model"] = model, ["prompt"] = prompt, ["n"] = 1, ["size"] = size }.ToJsonString());
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Check(response, text, ApiException.Header(response, "X-Request-ID"));
        var image = (ParseObject(text)?["data"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (Str(image?["b64_json"]) is { Length: > 0 } base64) return Convert.FromBase64String(base64);
        var url = Str(image?["url"]) ?? "";
        if (url.StartsWith("data:", StringComparison.Ordinal)) return Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? await Downloads.GetByteArrayAsync(uri, ct)
            : throw new ApiException("Сервер не вернул изображение: " + ApiException.Truncate(text, 300));
    }

    public async Task<BalanceInfo> GetBalanceAsync(CancellationToken ct) =>
        (await GetJsonAsync("/balance", ct))["balance"] is JsonValue balance ? new(Long(balance)) : throw new ApiException("Сервер не сообщил баланс.");

    public async Task<UsagePage> GetUsageAsync(string? cursor, CancellationToken ct)
    {
        var root = await GetJsonAsync("/usage?limit=250" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : ""), ct);
        List<UsageEntry> entries = [];
        foreach (var item in (root["data"] as JsonArray)?.OfType<JsonObject>() ?? [])
            if (DateTime.TryParse(Str(item["created_at"]), null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
                entries.Add(new(Str(item["model"]) ?? "", string.Equals(Str(item["status"]), "success", StringComparison.OrdinalIgnoreCase),
                    Long(item["total_tokens"]), Long(item["client_cost"]), created));
        return new(entries, Bool(root["has_more"]) == true ? Str(root["next_cursor"]) : null);
    }

    HttpRequestMessage PostRequest(string path, string json) =>
        new(HttpMethod.Post, _baseUrl + path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    async Task<JsonObject> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(_baseUrl + path, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Check(response, text, null);
        return ParseObject(text) ?? throw new ApiException("Сервер вернул не JSON: " + ApiException.Truncate(text, 200));
    }

    void Check(HttpResponseMessage response, string text, string? requestId)
    {
        if (_groq && Groq.LimitsFrom(response) is { } limits)
        {
            Limits = limits;
            LimitsChanged?.Invoke();
        }
        if (response.IsSuccessStatusCode) return;
        var wait = response.Headers.RetryAfter?.Delta;
        throw (_groq ? Groq.Explain(response.StatusCode, text, wait) : null) ?? ApiException.FromResponse(response.StatusCode, text, requestId, wait);
    }

    public void Dispose() => _http.Dispose();
}
