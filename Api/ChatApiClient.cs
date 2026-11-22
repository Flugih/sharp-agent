namespace SharpAgent.Api;

public sealed record UsageInfo(long PromptTokens, long CompletionTokens, long CachedTokens = 0, long ReasoningTokens = 0, long Requests = 1)
{
    internal UsageInfo Add(UsageInfo other) => new(PromptTokens + other.PromptTokens, CompletionTokens + other.CompletionTokens,
        CachedTokens + other.CachedTokens, ReasoningTokens + other.ReasoningTokens, Requests + other.Requests);

    internal static UsageInfo Read(JsonObject data) => new(Long(data["prompt_tokens"]), Long(data["completion_tokens"]),
        Long(data["cached_tokens"]), Long(data["reasoning_tokens"]), Long(data["requests"]));

    internal void Save(JsonObject data)
    {
        data["prompt_tokens"] = PromptTokens;
        data["completion_tokens"] = CompletionTokens;
        data["requests"] = Requests;
        if (CachedTokens > 0) data["cached_tokens"] = CachedTokens;
        if (ReasoningTokens > 0) data["reasoning_tokens"] = ReasoningTokens;
    }
}
public sealed record ModelProgress(string? Reasoning = null, string? Commentary = null);
public sealed record ChatResult(JsonObject Message, string? FinishReason, UsageInfo? Usage, string? RequestId, ModelProgress? Progress = null,
    string? Phase = null);
public sealed record ModelInfo(string Id, long? ContextTokens, bool? Vision, bool Chat, bool Images, string[] Efforts, string? Name = null);
public sealed record BalanceInfo(long Balance);
public sealed record UsageEntry(string Model, bool Success, long TotalTokens, long Cost, DateTime CreatedUtc);
public sealed record UsagePage(List<UsageEntry> Entries, string? NextCursor);

public interface IChatProvider : IDisposable
{
    Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools = true, Action<int, int>? retrying = null);
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
        // Gemini wraps the error in a one-element array; Mistral puts its fields at the top level.
        var root = ParseObject(body) ?? (body.Trim() is ['[', .. var inner, ']'] ? ParseObject(inner) : null);
        var error = root?["error"] as JsonObject ?? root;
        string? Field(string name) => Str(error?[name]);
        var details = (error?["details"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        bool badKey = details.Any(detail => Str(detail["reason"]) == "API_KEY_INVALID");
        string? title = Field("title") ?? (badKey ? "Провайдер не принял API-ключ. Проверьте его в настройках" : null),
            message = error?["message"] is JsonObject nested && nested["detail"] is JsonArray problems
                ? string.Join("; ", problems.Select(problem => Str(problem?["msg"])).OfType<string>()) : Field("message"),
            code = Field("status") ?? Field("code") ?? Field("type");
        retryAfter ??= details.Select(detail => Str(detail["retryDelay"])).FirstOrDefault(delay => delay != null) is { } delay &&
            double.TryParse(delay.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
        int number = (int)status;
        if (badKey) status = HttpStatusCode.Unauthorized;
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

    internal static async Task<ChatResult> RetryAsync(Func<CancellationToken, Action, Task<ChatResult>> send, Func<ApiException, bool?> fallback,
        Action reset, Action<int, int>? retrying, CancellationToken ct)
    {
        const int maxAttempts = 5;
        int requests = 0;
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                requests++;
                var result = await SendWithTimeoutAsync(send, ct);
                return result.Usage is { } usage ? result with { Usage = usage with { Requests = requests } } : result;
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                var adapted = ex is ApiException api ? fallback(api) : false;
                if (adapted == true)
                {
                    attempt--;
                    continue;
                }
                if (adapted == null || attempt >= maxAttempts || RetryDelay(ex, attempt, ct) is not { } delay) throw;
                reset();
                retrying?.Invoke(attempt + 1, maxAttempts);
                await Task.Delay(delay, ct);
            }
        }
    }

    static async Task<ChatResult> SendWithTimeoutAsync(Func<CancellationToken, Action, Task<ChatResult>> send, CancellationToken ct)
    {
        const int idleSeconds = 90;
        var idleTimeout = TimeSpan.FromSeconds(idleSeconds);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void ReportActivity() => idle.CancelAfter(idleTimeout);
        ReportActivity();
        try
        {
            return await send(idle.Token, ReportActivity);
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ApiException($"Модель не присылала данные ответа {idleSeconds} секунд. Нажмите «Повторить».", retryable: true);
        }
    }

    static TimeSpan? RetryDelay(Exception ex, int attempt, CancellationToken ct) => ex switch
    {
        ApiException { Retryable: true, RetryAfter: { } wait } => wait <= MaxWait ? wait : null,
        ApiException { Retryable: true } => TimeSpan.FromSeconds(Math.Min(MaxWait.TotalSeconds, 2 * attempt * attempt)),
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
    static readonly HttpClient Downloads = AppNetwork.Client(TimeSpan.FromMinutes(2), 64 << 20);
    readonly string _baseUrl = baseUrl.TrimEnd('/');
    readonly bool _groq = provider == AgentProvider.Groq;
    readonly HttpClient _http = AppNetwork.Client(RequestTimeout, headers: headers =>
    {
        headers.Authorization = new("Bearer", apiKey);
        headers.UserAgent.Add(new("SharpAgent", "1.0"));
        headers.Accept.Add(new("application/json"));
    });
    readonly HashSet<string> _noStreaming = new(StringComparer.OrdinalIgnoreCase), _noStreamOptions = new(StringComparer.OrdinalIgnoreCase);

    public event Action? LimitsChanged;
    public GroqLimits? Limits { get; private set; }

    public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools, Action<int, int>? retrying)
    {
        var body = new JsonObject
        {
            ["model"] = model, ["messages"] = RequestMessages(messages),
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = callTools ? "auto" : "none";
        }
        if (maxTokens is > 0) body["max_tokens"] = maxTokens.Value;
        if (effort != null)
        {
            if (provider == AgentProvider.OpenRouter) body["reasoning"] = new JsonObject { ["effort"] = effort };
            else body["reasoning_effort"] = effort;
        }
        // Gemini accepts either reasoning_effort or thinking_config, never both.
        else if (provider == AgentProvider.Gemini && GeminiThinking(model))
            body["extra_body"] = new JsonObject
            {
                ["google"] = new JsonObject { ["thinking_config"] = new JsonObject { ["include_thoughts"] = true } },
            };
        bool stream = !_noStreaming.Contains(model), options = provider != AgentProvider.Mistral && stream && !_noStreamOptions.Contains(model);
        if (options) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        void Reset()
        {
            reportText(null, false);
            reportProgress(new());
        }
        return ApiException.RetryAsync((attemptToken, reportActivity) =>
        {
            body["stream"] = stream;
            return SendCompletionAsync(body.ToJsonString(), stream, reportText, reportProgress, attemptToken, reportActivity);
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
        }, Reset, retrying, ct);
    }

    JsonArray RequestMessages(IReadOnlyList<JsonNode> messages)
    {
        var result = new JsonArray((_groq ? Groq.Clean(messages) : messages.Select(m => m.DeepClone())).ToArray());
        if (provider != AgentProvider.Mistral) return result;
        var calls = result.OfType<JsonObject>().SelectMany(m => (m["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? []).ToArray();
        var used = calls.Select(call => Str(call["id"])).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        int next = 0;
        foreach (var call in calls)
        {
            if (Str(call["id"]) is not { } id || id.Length == 9 && id.All(char.IsAsciiLetterOrDigit)) continue;
            if (!ids.TryGetValue(id, out var mapped))
            {
                do { mapped = "sa" + (++next).ToString("D7", CultureInfo.InvariantCulture); } while (!used.Add(mapped));
                ids[id] = mapped;
            }
            call["id"] = mapped;
        }
        foreach (var message in result.OfType<JsonObject>())
            if (Str(message["role"]) == "tool" && Str(message["tool_call_id"]) is { } id && ids.TryGetValue(id, out var mapped))
                message["tool_call_id"] = mapped;
        // Mistral rejects a user message right after a tool result (error 3230).
        for (int i = result.Count - 1; i > 0; i--)
            if (Str(result[i]?["role"]) == "user" && Str(result[i - 1]?["role"]) == "tool")
                result.Insert(i, new JsonObject { ["role"] = "assistant", ["content"] = " " });
        return result;
    }

    async Task<ChatResult> SendCompletionAsync(string json, bool stream, Action<string?, bool> reportText, Action<ModelProgress> reportProgress,
        CancellationToken ct, Action reportActivity)
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
                return await ChatCompletionStream.ReadAsync(response, requestId, reportText, reportProgress, timeout.Token, reportActivity);
            }
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            Check(response, text, requestId);
            JsonObject? root;
            try { root = JsonNode.Parse(text) as JsonObject; }
            catch (JsonException) { throw new ApiException("Сервер вернул не JSON: " + ApiException.Truncate(text, 400)); }
            if (root?["error"] is JsonObject) throw ApiException.FromResponse(response.StatusCode, text, requestId);
            if ((root?["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice || choice["message"] is not JsonObject message)
                throw new ApiException("Неожиданный ответ сервера (нет choices[0].message): " + ApiException.Truncate(text, 400));
            var answer = (JsonObject)message.DeepClone();
            var thought = ChatCompletionStream.ThinkingOf(answer["content"]);
            if (thought != null) answer["content"] = AgentRunner.ExtractText(answer["content"]);
            else if (answer["content"] is JsonValue content && Str(content) is { } plain &&
                ChatCompletionStream.ThoughtTags.Split(plain) is { Thought.Length: > 0 } tagged)
            {
                answer["content"] = tagged.Text is { Length: > 0 } reply ? reply : null;
                thought = tagged.Thought;
            }
            return new(answer, Str(choice["finish_reason"]), ChatCompletionStream.Usage(root), requestId, thought != null ? new(Reasoning: thought) : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("Модель не ответила за отведённое время. Нажмите «Повторить».", retryable: true);
        }
    }

    internal async Task CheckOpenRouterKeyAsync(CancellationToken ct) => _ = await GetJsonAsync("/key", ct);

    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        List<ModelInfo> result = [];
        if ((await GetJsonAsync("/models", ct))["data"] is not JsonArray data) return result;
        foreach (var item in data)
        {
            var obj = item as JsonObject;
            var id = Str(obj != null ? obj["id"] : item);
            if (provider == AgentProvider.Gemini && id?.StartsWith("models/", StringComparison.Ordinal) == true) id = id["models/".Length..];
            if (string.IsNullOrWhiteSpace(id) || result.Any(m => m.Id == id) || _groq && !Groq.IsChatModel(id, obj)) continue;
            var caps = obj?["capabilities"] as JsonObject;
            var endpoints = (caps?["endpoints"] as JsonArray)?.Select(Str).ToList();
            bool images = endpoints?.Contains("images") == true;
            long context = ContextKeys.Select(key => Long(obj?[key])).FirstOrDefault(tokens => tokens > 0);
            if (provider == AgentProvider.Gemini)
            {
                bool chat = id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) &&
                    !new[] { "embedding", "-tts", "-image", "banana", "live", "robotics", "computer-use", "native-audio", "aqa" }.Any(id.Contains);
                if (chat) result.Add(new(id, context > 0 ? context : null, true, true, false,
                    GeminiThinking(id) ? StandardEfforts : [], Str(obj?["name"])));
                continue;
            }
            if (provider == AgentProvider.OpenRouter)
            {
                var parameters = Values(obj?["supported_parameters"]);
                var architecture = obj?["architecture"] as JsonObject;
                var outputs = Values(architecture?["output_modalities"]);
                var inputs = Values(architecture?["input_modalities"]);
                var levels = Values((obj?["reasoning"] as JsonObject)?["supported_efforts"]).Intersect(AppConfig.EffortLevels).ToArray();
                bool chat = (outputs.Length == 0 || outputs.Contains("text")) && (parameters.Contains("tools") || id == "openrouter/auto");
                result.Add(new(id, context > 0 ? context : null, inputs.Length == 0 ? null : inputs.Contains("image"), chat, false,
                    levels.Length > 0 ? levels : parameters.Contains("reasoning") ? StandardEfforts : [], Str(obj?["name"])));
                continue;
            }
            if (provider == AgentProvider.Mistral)
            {
                if (context == 0) context = Long(obj?["max_context_length"]);
                bool chat = Bool(caps?["completion_chat"]) != false && Bool(caps?["function_calling"]) != false;
                bool thinking = Bool(caps?["reasoning"]) == true || new[] { "mistral-small-latest", "mistral-small-2603", "mistral-medium-latest", "mistral-medium-3-5" }.Contains(id);
                result.Add(new(id, context > 0 ? context : null, Bool(caps?["vision"]), chat, false,
                    thinking ? ["high"] : [], Str(obj?["name"])));
                continue;
            }
            result.Add(new(id, context > 0 ? context : null, Bool(caps?["vision"]), (endpoints?.Contains("chat") ?? !images) && Bool(caps?["tools"]) != false,
                images, _groq ? Groq.EffortsOf(id) : Bool(caps?["reasoning_effort"]) == true &&
                    (caps?["reasoning_endpoints"] as JsonArray)?.Select(Str).Contains("chat") == true ? StandardEfforts : []));
        }
        result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    static string[] Values(JsonNode? source) => source is JsonArray items ? items.Select(Str).OfType<string>().ToArray() : [];

    static bool GeminiThinking(string model) => Regex.IsMatch(model, @"^gemini-(?:2\.5|[3-9])", RegexOptions.IgnoreCase);

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
