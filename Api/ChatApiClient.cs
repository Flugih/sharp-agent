using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpAgent.Core;

namespace SharpAgent.Api;

public sealed record UsageInfo(long PromptTokens, long CompletionTokens);

public sealed record ModelProgress(string? Reasoning = null, string? Commentary = null);

public sealed record ChatResult(JsonObject Message, string? FinishReason, UsageInfo? Usage, string? RequestId,
    ModelProgress? Progress = null, string? Phase = null);

public sealed record ModelInfo(string Id, long? ContextTokens, bool? Vision, bool Chat, bool Images, string[] Efforts, string? Name = null);

public sealed record BalanceInfo(long Balance);

public sealed record UsageEntry(string Model, bool Success, long TotalTokens, long Cost, DateTime CreatedUtc);

public sealed record UsagePage(List<UsageEntry> Entries, string? NextCursor);

public interface IChatProvider : IDisposable
{
    event Action<int, int>? Retrying;

    Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens,
        string? effort, Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct);

    Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct);
}

public sealed class ApiException(string message, HttpStatusCode? status = null, bool retryable = false, string? code = null,
    TimeSpan? retryAfter = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = status;
    public bool Retryable { get; } = retryable;
    public string? Code { get; } = code;
    public string? Parameter { get; init; }
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static ApiException FromResponse(HttpStatusCode status, string body, string? requestId, TimeSpan? retryAfter = null)
    {
        string? title = null, message = null, action = null, billing = null, code = null, parameter = null;
        bool? retryable = null;
        try
        {
            if (JsonNode.Parse(body)?["error"] is JsonObject err)
            {
                title = JsonUtil.Str(err["title"]);
                message = JsonUtil.Str(err["message"]);
                action = JsonUtil.Str(err["action"]);
                billing = JsonUtil.Str(err["billing"]);
                code = JsonUtil.Str(err["code"]) ?? JsonUtil.Str(err["type"]);
                parameter = JsonUtil.Str(err["param"]);
                retryable = JsonUtil.Bool(err["retryable"]);
                requestId ??= JsonUtil.Str(err["request_id"]);
            }
        }
        catch (JsonException) { }

        int statusCode = (int)status;
        var sb = new StringBuilder(title ?? $"Сервер ответил ошибкой (HTTP {statusCode})");
        if (message != null) sb.Append(": ").Append(message);
        else if (title == null && !string.IsNullOrWhiteSpace(body) && !LooksLikeHtml(body))
            sb.Append(": ").Append(Truncate(body.Trim(), 400));
        if (action != null) sb.Append('\n').Append(action);
        if (billing != null) sb.Append('\n').Append(billing);
        sb.Append(Tail(statusCode, code, requestId));

        bool canRetry = retryable ?? (statusCode == 429 || statusCode >= 500);
        return new ApiException(sb.ToString(), status, canRetry, code, retryAfter) { Parameter = parameter };
    }

    internal bool RejectsStream(bool options = false) =>
        StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented &&
        (Parameter == (options ? "stream_options" : "stream") ||
         Regex.IsMatch(Message, options ? @"\b(stream_options|include_usage)\b" : @"\bstream(?:ing)?\b", RegexOptions.IgnoreCase));

    internal static string Tail(int? status, string? code, string? requestId)
    {
        var parts = new[]
        {
            status is { } number ? "HTTP " + number : null,
            code != null ? "код: " + code : null,
            requestId != null ? "request_id: " + requestId : null,
        }.OfType<string>().ToList();
        return parts.Count > 0 ? $"\n[{string.Join(", ", parts)}]" : "";
    }

    internal static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    internal static TimeSpan? RetryDelay(Exception ex, int attempt, CancellationToken ct) => ex switch
    {
        ApiException { Retryable: true, RetryAfter: { } wait } => wait <= MaxWait ? wait : null,
        ApiException { Retryable: true } => TimeSpan.FromSeconds(2 * attempt * attempt),
        HttpRequestException or IOException => TimeSpan.FromSeconds(2 * attempt),
        TaskCanceledException when !ct.IsCancellationRequested => TimeSpan.Zero,
        _ => null,
    };

    private static bool LooksLikeHtml(string body) => body.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal);

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public sealed class ChatApiClient : IChatProvider
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);
    private static readonly string[] ContextKeys = ["safe_context_tokens", "context_window_tokens", "context_window", "context_length"];
    private static readonly string[] StandardEfforts = ["low", "medium", "high"];
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly bool _groq;
    private readonly HashSet<string> _noStreaming = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noStreamOptions = new(StringComparer.OrdinalIgnoreCase);

    public event Action<int, int>? Retrying;
    public event Action? LimitsChanged;

    public GroqLimits? Limits { get; private set; }

    public ChatApiClient(string baseUrl, string apiKey, AgentProvider provider = AgentProvider.Tooken)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _groq = provider == AgentProvider.Groq;
        _http = new HttpClient { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SharpAgent/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages,
        JsonArray? tools, int? maxTokens, string? effort, Action<string?, bool> reportText, Action<ModelProgress> reportProgress,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray((_groq ? Groq.Clean(messages) : messages.Select(m => m.DeepClone())).ToArray()),
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        if (maxTokens is > 0) body["max_tokens"] = maxTokens.Value;
        if (effort != null) body["reasoning_effort"] = effort;
        bool stream = !_noStreaming.Contains(model);
        bool options = stream && !_noStreamOptions.Contains(model);
        if (options) body["stream_options"] = new JsonObject { ["include_usage"] = true };

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                body["stream"] = stream;
                return await SendCompletionAsync(body.ToJsonString(), stream, reportText, reportProgress, ct);
            }
            catch (ApiException ex) when (options && ex.RejectsStream(options: true))
            {
                options = false;
                _noStreamOptions.Add(model);
                body.Remove("stream_options");
                attempt--;
            }
            catch (ApiException ex) when (stream && ex.RejectsStream())
            {
                stream = options = false;
                _noStreaming.Add(model);
                body.Remove("stream_options");
                reportText(null, false);
                reportProgress(new());
                attempt--;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ApiException.RetryDelay(ex, attempt, ct) is { } delay)
            {
                reportText(null, false);
                reportProgress(new());
                Retrying?.Invoke(attempt + 1, MaxAttempts);
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task<ChatResult> SendCompletionAsync(string json, bool stream, Action<string?, bool> reportText,
        Action<ModelProgress> reportProgress, CancellationToken ct)
    {
        using var request = PostRequest("/chat/completions", json);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (stream) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var requestId = RequestId(response);
            if (response.IsSuccessStatusCode &&
                string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                Check(response, "", requestId);
                return await ChatCompletionStream.ReadAsync(response, requestId, reportText, reportProgress, timeout.Token);
            }
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            Check(response, text, requestId);
            return ParseCompletion(text, requestId, response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("Модель не ответила за отведённое время. Нажмите «Повторить».", retryable: true);
        }
    }

    private static ChatResult ParseCompletion(string text, string? requestId, HttpStatusCode status)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException) { throw new ApiException("Сервер вернул не JSON: " + ApiException.Truncate(text, 400)); }

        if (root?["error"] is JsonObject)
            throw ApiException.FromResponse(status, text, requestId);

        if ((root?["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice || choice["message"] is not JsonObject message)
            throw new ApiException("Неожиданный ответ сервера (нет choices[0].message): " + ApiException.Truncate(text, 400));

        return new ChatResult((JsonObject)message.DeepClone(), JsonUtil.Str(choice["finish_reason"]),
            ChatCompletionStream.Usage(root as JsonObject), requestId);
    }

    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var result = new List<ModelInfo>();
        if ((await GetJsonAsync("/models", ct))?["data"] is not JsonArray data) return result;

        foreach (var item in data)
        {
            var obj = item as JsonObject;
            var id = obj != null ? JsonUtil.Str(obj["id"]) : JsonUtil.Str(item);
            if (string.IsNullOrWhiteSpace(id) || result.Any(m => m.Id == id)) continue;
            if (_groq && !Groq.IsChatModel(id, obj)) continue;

            var caps = obj?["capabilities"] as JsonObject;
            var endpoints = (caps?["endpoints"] as JsonArray)?.Select(JsonUtil.Str).ToList();
            bool images = endpoints?.Contains("images") == true;
            bool chat = (endpoints?.Contains("chat") ?? !images) && JsonUtil.Bool(caps?["tools"]) != false;
            bool effort = JsonUtil.Bool(caps?["reasoning_effort"]) == true &&
                          (caps?["reasoning_endpoints"] as JsonArray)?.Select(JsonUtil.Str).Contains("chat") == true;
            long context = ContextKeys.Select(key => JsonUtil.Long(obj?[key])).FirstOrDefault(tokens => tokens > 0);
            result.Add(new ModelInfo(id, context > 0 ? context : null, JsonUtil.Bool(caps?["vision"]), chat, images,
                _groq ? Groq.EffortsOf(id) : effort ? StandardEfforts : []));
        }
        result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public async Task<byte[]> GenerateImageAsync(string model, string prompt, string size, CancellationToken ct)
    {
        var json = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["n"] = 1, ["size"] = size }.ToJsonString();
        var (text, _, _) = await PostAsync("/images/generations", json, ct);
        var image = (JsonNode.Parse(text)?["data"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (JsonUtil.Str(image?["b64_json"]) is { Length: > 0 } base64) return Convert.FromBase64String(base64);
        var url = JsonUtil.Str(image?["url"]) ?? "";
        if (url.StartsWith("data:", StringComparison.Ordinal)) return Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            return await Downloads.GetByteArrayAsync(uri, ct);
        throw new ApiException("Сервер не вернул изображение: " + ApiException.Truncate(text, 300));
    }

    public async Task<BalanceInfo> GetBalanceAsync(CancellationToken ct) =>
        (await GetJsonAsync("/balance", ct))?["balance"] is JsonValue balance
            ? new BalanceInfo(JsonUtil.Long(balance))
            : throw new ApiException("Сервер не сообщил баланс.");

    public async Task<UsagePage> GetUsageAsync(string? cursor, CancellationToken ct)
    {
        var path = "/usage?limit=250" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
        var root = await GetJsonAsync(path, ct);
        var entries = new List<UsageEntry>();
        if (root?["data"] is JsonArray data)
        {
            foreach (var item in data.OfType<JsonObject>())
            {
                if (!DateTime.TryParse(JsonUtil.Str(item["created_at"]), null,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
                    continue;
                entries.Add(new UsageEntry(
                    JsonUtil.Str(item["model"]) ?? "",
                    string.Equals(JsonUtil.Str(item["status"]), "success", StringComparison.OrdinalIgnoreCase),
                    JsonUtil.Long(item["total_tokens"]),
                    JsonUtil.Long(item["client_cost"]),
                    created));
            }
        }
        bool hasMore = JsonUtil.Bool(root?["has_more"]) == true;
        return new UsagePage(entries, hasMore ? JsonUtil.Str(root?["next_cursor"]) : null);
    }

    private HttpRequestMessage PostRequest(string path, string json) =>
        new(HttpMethod.Post, _baseUrl + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string? RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Request-ID", out var ids) ? ids.FirstOrDefault() : null;

    private async Task<(string Text, string? RequestId, HttpStatusCode Status)> PostAsync(string path, string json,
        CancellationToken ct)
    {
        using var request = PostRequest(path, json);
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var requestId = RequestId(response);
        Check(response, text, requestId);
        return (text, requestId, response.StatusCode);
    }

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(_baseUrl + path, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Check(response, text, null);
        try { return JsonNode.Parse(text); }
        catch (JsonException) { throw new ApiException("Сервер вернул не JSON: " + ApiException.Truncate(text, 200)); }
    }

    private void Check(HttpResponseMessage response, string text, string? requestId)
    {
        if (_groq && Groq.LimitsFrom(response.Headers) is { } limits)
        {
            Limits = limits;
            LimitsChanged?.Invoke();
        }
        if (response.IsSuccessStatusCode) return;
        var wait = response.Headers.RetryAfter?.Delta;
        throw (_groq ? Groq.Explain(response.StatusCode, text, wait) : null)
              ?? ApiException.FromResponse(response.StatusCode, text, requestId, wait);
    }

    public void Dispose() => _http.Dispose();
}
