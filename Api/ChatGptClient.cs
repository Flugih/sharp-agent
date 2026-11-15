using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using SharpAgent.Core;

namespace SharpAgent.Api;

public sealed class ChatGptClient : IChatProvider
{
    public const string UsageUrl = "https://chatgpt.com/settings/usage";
    internal const string LimitCode = "subscription_sharing_usage_limit_exceeded", InvalidUserCode = "subscription_sharing_invalid_user";

    private const int MaxAttempts = 3;
    private const string Namespace = "functions";
    private const string IneligibleCode = "subscription_sharing_user_not_eligible";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(300);

    private readonly ChatGptAuth _auth;
    private readonly HashSet<string> _noSummaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noStreaming = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _baseUrl = "https://api.openai.com/v1";
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestHeaders = { { "User-Agent", ChatGptAuth.UserAgent } } };

    public ChatGptClient(ChatGptAuth auth) => _auth = auth;

    public event Action<int, int>? Retrying;
    public event Action? LimitsChanged;

    public string? LimitMessage { get; private set; }

    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models"),
            HttpCompletionOption.ResponseContentRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw Explain(response, text);
        var root = JsonUtil.ParseObject(text) ?? throw new ApiException("ChatGPT вернул не JSON: " + ApiException.Truncate(text, 200));
        var models = new List<ModelInfo>();
        foreach (var item in (root["models"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (JsonUtil.Str(item["visibility"]) != "list" || JsonUtil.Str(item["slug"]) is not { Length: > 0 } slug) continue;
            var efforts = (item["supported_reasoning_levels"] as JsonArray)?
                .Select(level => JsonUtil.Str(level is JsonObject entry ? entry["effort"] : level) ?? "")
                .Where(AppConfig.EffortLevels.Contains).Distinct().ToArray() ?? [];
            long context = JsonUtil.Long(item["context_window"]);
            bool? vision = item["input_modalities"] is JsonArray kinds ? kinds.Any(kind => JsonUtil.Str(kind) == "image") : null;
            models.Add(new ModelInfo(slug, context > 0 ? context : null, vision, true, false, efforts, JsonUtil.Str(item["display_name"])));
        }
        return models;
    }

    public async Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens,
        string? effort, Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct)
    {
        bool summary = !_noSummaries.Contains(model);
        bool streaming = !_noStreaming.Contains(model);
        var body = Request(model, messages, tools, effort, summary);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                body["stream"] = streaming;
                var result = await SendCompletionAsync(body.ToJsonString(), streaming, reportText, reportProgress, ct);
                SetLimit(null);
                return result;
            }
            catch (ApiException ex) when (summary && SummaryRejected(ex, effort != null))
            {
                _noSummaries.Add(model);
                summary = false;
                if (body["reasoning"] is JsonObject reasoning) reasoning.Remove("summary");
                if (effort == null) body.Remove("reasoning");
                reportText(null, false);
                reportProgress(new());
                attempt--;
            }
            catch (ApiException ex) when (streaming && ex.RejectsStream())
            {
                _noStreaming.Add(model);
                streaming = false;
                reportText(null, false);
                reportProgress(new());
                attempt--;
            }
            catch (ApiException ex) when (ex.Code is LimitCode or IneligibleCode)
            {
                SetLimit(ex.Message.Split('\n')[0]);
                throw;
            }
            catch (ApiException ex) when (ex.Code == "content_filter")
            {
                reportText(null, false);
                reportProgress(new());
                throw;
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

    private static bool SummaryRejected(ApiException ex, bool hasEffort) =>
        ex.StatusCode is null or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity &&
        (ex.Parameter == "reasoning.summary" || !hasEffort && ex.Parameter == "reasoning" ||
         ex.Message.Contains("reasoning.summary", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("reasoning summar", StringComparison.OrdinalIgnoreCase));

    private static JsonObject Request(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, string? effort, bool summary)
    {
        var functions = new JsonArray();
        foreach (var function in tools?.OfType<JsonObject>().Select(tool => tool["function"]).OfType<JsonObject>() ?? [])
            functions.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = function["name"]?.DeepClone(),
                ["description"] = function["description"]?.DeepClone(),
                ["strict"] = false,
                ["parameters"] = function["parameters"]?.DeepClone(),
            });

        var input = new JsonArray();
        if (functions.Count > 0)
            input.Add(new JsonObject
            {
                ["type"] = "additional_tools",
                ["role"] = "developer",
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["type"] = "namespace", ["name"] = Namespace, ["description"] = "", ["tools"] = functions,
                }),
            });
        foreach (var message in messages.OfType<JsonObject>()) AddInput(input, message);

        var body = new JsonObject { ["model"] = model, ["input"] = input, ["store"] = false };
        var reasoning = new JsonObject();
        if (effort != null) reasoning["effort"] = effort;
        if (summary) reasoning["summary"] = "auto";
        if (reasoning.Count > 0) body["reasoning"] = reasoning;
        return body;
    }

    private static void AddInput(JsonArray input, JsonObject message)
    {
        var text = Text(message["content"]);
        switch (JsonUtil.Str(message["role"]))
        {
            case "system" or "developer":
                input.Add(Message("developer", "input_text", text));
                break;
            case "user":
                input.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = UserContent(message["content"]) });
                break;
            case "assistant":
                if (JsonUtil.Str(message["commentary"]) is { Length: > 0 } commentary)
                    input.Add(AssistantMessage(commentary, "commentary"));
                if (!string.IsNullOrEmpty(text)) input.Add(AssistantMessage(text, JsonUtil.Str(message["phase"])));
                foreach (var call in (message["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var function = call["function"] as JsonObject;
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["name"] = JsonUtil.Str(function?["name"]) ?? "",
                        ["namespace"] = Namespace,
                        ["arguments"] = JsonUtil.Str(function?["arguments"]) ?? "{}",
                        ["call_id"] = JsonUtil.Str(call["id"]) ?? "",
                    });
                }
                break;
            case "tool":
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output", ["call_id"] = JsonUtil.Str(message["tool_call_id"]) ?? "", ["output"] = text ?? "",
                });
                break;
        }
    }

    private static JsonArray UserContent(JsonNode? content)
    {
        if (content is not JsonArray parts) return new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = Text(content) ?? "" });
        var result = new JsonArray();
        foreach (var part in parts.OfType<JsonObject>())
        {
            if (JsonUtil.Str(part["type"]) == "image_url")
                result.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = JsonUtil.Str((part["image_url"] as JsonObject)?["url"]) ?? JsonUtil.Str(part["image_url"]),
                });
            else if (JsonUtil.Str(part["text"]) is { } text)
                result.Add(new JsonObject { ["type"] = "input_text", ["text"] = text });
        }
        return result;
    }

    private static JsonObject Message(string role, string type, string? text) => new()
    {
        ["type"] = "message", ["role"] = role, ["content"] = new JsonArray(new JsonObject { ["type"] = type, ["text"] = text ?? "" }),
    };

    private static JsonObject AssistantMessage(string text, string? phase)
    {
        var message = Message("assistant", "output_text", text);
        if (phase is "commentary" or "final_answer") message["phase"] = phase;
        return message;
    }

    private static string? Text(JsonNode? content) => content switch
    {
        null => null,
        JsonArray parts => string.Concat(parts.OfType<JsonObject>().Select(part => JsonUtil.Str(part["text"]) ?? JsonUtil.Str(part["refusal"]))),
        _ => JsonUtil.Str(content),
    };

    private async Task<ChatResult> SendCompletionAsync(string body, bool streaming, Action<string?, bool> reportText,
        Action<ModelProgress> reportProgress, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/responses")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(streaming ? "text/event-stream" : "application/json"));
            return request;
        }, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw Explain(response, await response.Content.ReadAsStringAsync(ct));
        var requestId = RequestId(response);
        if (!streaming || response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var answer = JsonUtil.ParseObject(await response.Content.ReadAsStringAsync(ct))
                ?? throw new ApiException("ChatGPT вернул ответ в неизвестном формате. Нажмите «Повторить».");
            return FinishResponse([], answer, requestId);
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var items = new List<JsonObject>();
        var summaries = new SortedDictionary<(long Output, long Part), string>();
        var commentaryItems = new HashSet<long>();
        var commentary = new List<string>();
        void ReportProgress() => reportProgress(new(JoinSummaries(summaries.Values), JoinSummaries(commentary)));
        long? textItem = null;
        bool calls = false;
        while (true)
        {
            idle.CancelAfter(IdleTimeout);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ApiException("ChatGPT перестал отвечать посреди ответа. Нажмите «Повторить».", retryable: true);
            }
            if (line == null)
                throw new ApiException("Связь с ChatGPT оборвалась до конца ответа. Нажмите «Повторить».", retryable: true);
            if (!line.StartsWith("data:", StringComparison.Ordinal) || JsonUtil.ParseObject(line[5..]) is not { } data) continue;

            var answer = data["response"] as JsonObject;
            var output = JsonUtil.Long(data["output_index"]);
            switch (JsonUtil.Str(data["type"]))
            {
                case "response.output_item.added" when IsCommentary(data["item"] as JsonObject):
                    commentaryItems.Add(output);
                    break;
                case "response.output_text.delta" or "response.refusal.delta" when JsonUtil.Str(data["delta"]) is { Length: > 0 } delta:
                    if (commentaryItems.Contains(output)) break;
                    reportText(textItem != null && textItem != output ? "\n\n" + delta : delta, calls);
                    textItem = output;
                    break;
                case "response.output_item.added" when JsonUtil.Str((data["item"] as JsonObject)?["type"]) == "function_call":
                    calls = true;
                    reportText("", true);
                    break;
                case "response.reasoning_summary_text.done" when JsonUtil.Str(data["text"]) is { } summary:
                    summaries[(output, JsonUtil.Long(data["summary_index"]))] = summary;
                    ReportProgress();
                    break;
                case "response.output_item.done" when data["item"] is JsonObject item:
                    items.Add(item);
                    if (IsCommentary(item) && Text(item["content"]) is { Length: > 0 } comment)
                    {
                        if (!commentaryItems.Contains(output) && textItem == output)
                        {
                            reportText(null, false);
                            textItem = null;
                        }
                        commentary.Add(comment);
                        ReportProgress();
                    }
                    if (JsonUtil.Str(item["type"]) == "reasoning" && item["summary"] is JsonArray parts)
                    {
                        for (int i = 0; i < parts.Count; i++)
                            if (parts[i] is JsonObject part && JsonUtil.Str(part["text"]) is { } text)
                                summaries[(output, i)] = text;
                        ReportProgress();
                    }
                    break;
                case "response.completed":
                    return FinishResponse(items, answer, requestId, summaries.Values);
                case "response.incomplete":
                    return FinishResponse(items, answer, requestId, summaries.Values, incomplete: true);
                case "response.failed":
                    throw Failed(answer?["error"] as JsonObject, requestId);
                case "error":
                    throw Failed(data["error"] as JsonObject ?? data, requestId);
            }
        }
    }

    private static ChatResult FinishResponse(List<JsonObject> items, JsonObject? answer, string? requestId,
        IEnumerable<string>? summaries = null, bool incomplete = false)
    {
        var status = JsonUtil.Str(answer?["status"]);
        var error = answer?["error"] as JsonObject;
        if (status == "failed" || error != null) throw Failed(error, requestId);
        var reason = JsonUtil.Str((answer?["incomplete_details"] as JsonObject)?["reason"]);
        if (reason == "content_filter") throw Filtered(requestId);
        if (answer?["output"] is JsonArray { Count: > 0 } output) items = output.OfType<JsonObject>().ToList();

        var texts = new List<string>();
        var commentary = new List<string>();
        var calls = new JsonArray();
        string? phase = null;
        foreach (var item in items)
        {
            switch (JsonUtil.Str(item["type"]))
            {
                case "message" when Text(item["content"]) is { Length: > 0 } text:
                    if (IsCommentary(item)) commentary.Add(text);
                    else
                    {
                        texts.Add(text);
                        phase ??= JsonUtil.Str(item["phase"]);
                    }
                    break;
                case "function_call":
                    calls.Add(new JsonObject
                    {
                        ["id"] = JsonUtil.Str(item["call_id"]) is { Length: > 0 } id ? id : "call_" + Guid.NewGuid().ToString("N")[..16],
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = JsonUtil.Str(item["name"]) ?? "", ["arguments"] = JsonUtil.Str(item["arguments"]) ?? "{}",
                        },
                    });
                    break;
            }
        }

        var content = string.Join("\n\n", texts);
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content.Length > 0 ? content : null };
        if (calls.Count > 0) message["tool_calls"] = calls;
        var finalSummaries = items
            .Where(item => JsonUtil.Str(item["type"]) == "reasoning")
            .SelectMany(item => (item["summary"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(part => JsonUtil.Str(part["text"])).OfType<string>();
        var finish = incomplete || status == "incomplete" || reason != null ? "length" : calls.Count > 0 ? "tool_calls" : "stop";
        return new ChatResult(message, finish, Usage(answer), requestId ?? JsonUtil.Str(answer?["id"]),
            new(JoinSummaries(finalSummaries) ?? JoinSummaries(summaries ?? []), JoinSummaries(commentary)), phase);
    }

    private static bool IsCommentary(JsonObject? item) =>
        JsonUtil.Str(item?["type"]) == "message" && JsonUtil.Str(item?["phase"]) == "commentary";

    private static string? JoinSummaries(IEnumerable<string> summaries) =>
        string.Join("\n\n", summaries.Where(text => !string.IsNullOrWhiteSpace(text))) is { Length: > 0 } text ? text : null;

    private static UsageInfo? Usage(JsonObject? response) =>
        response?["usage"] is JsonObject usage
            ? new UsageInfo(JsonUtil.Long(usage["input_tokens"]), JsonUtil.Long(usage["output_tokens"]))
            : null;

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, HttpCompletionOption option, CancellationToken ct)
    {
        if (_auth.Connected && !_auth.PlanAllowed)
            throw new ApiException("Sharp Agent не получил разрешения использовать подписку ChatGPT. " +
                                   "Откройте настройки и нажмите «Разрешить использование подписки».", HttpStatusCode.Forbidden);
        for (bool recovered = false; ; recovered = true)
        {
            var token = await _auth.AccessAsync(ct);
            var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await _http.SendAsync(request, option, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || recovered) return response;
            response.Dispose();
            await _auth.RecoverAsync(token, ct);
        }
    }

    private void SetLimit(string? text)
    {
        if (text == LimitMessage) return;
        LimitMessage = text;
        LimitsChanged?.Invoke();
    }

    private static string? RequestId(HttpResponseMessage response) =>
        new[] { "x-request-id", "openai-request-id" }
            .Select(name => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null)
            .FirstOrDefault(id => id != null);

    private static ApiException Explain(HttpResponseMessage response, string text)
    {
        var root = JsonUtil.ParseObject(text);
        var error = root?["error"] as JsonObject;
        var detail = root?["detail"];
        var message = JsonUtil.Str(error?["message"]) ?? JsonUtil.Str((detail as JsonObject)?["message"] ?? detail);
        return Failure(response.StatusCode, JsonUtil.Str(error?["code"]), JsonUtil.Str(error?["param"]), message, RequestId(response),
            response.Headers.RetryAfter?.Delta);
    }

    private static ApiException Failed(JsonObject? error, string? requestId) =>
        Failure(null, JsonUtil.Str(error?["code"]) ?? JsonUtil.Str(error?["type"]), JsonUtil.Str(error?["param"]), JsonUtil.Str(error?["message"]),
            requestId, null);

    private static ApiException Filtered(string? requestId) =>
        new("ChatGPT остановил ответ фильтром содержимого. Переформулируйте запрос." + ApiException.Tail(null, "content_filter", requestId),
            code: "content_filter");

    private static ApiException Failure(HttpStatusCode? status, string? code, string? param, string? message, string? requestId, TimeSpan? wait)
    {
        int? number = (int?)status;
        var (text, retry, usual) = code switch
        {
            "context_length_exceeded" => ("Запрос не помещается в память модели (context_length_exceeded).", false, 400),
            IneligibleCode => ("Подписку ChatGPT этого аккаунта или рабочего пространства нельзя использовать в Sharp Agent. " +
                               "Выберите другого провайдера в меню модели.", false, 403),
            LimitCode => ("Достигнут лимит использования подписки ChatGPT — общий или заданный для Sharp Agent. Проверьте его " +
                          "в настройках ChatGPT («Управлять использованием») или выберите другого провайдера в меню модели.", false, 429),
            "subscription_sharing_usage_unavailable" => ("ChatGPT не смог проверить лимиты подписки. Подождите немного и нажмите «Повторить».",
                true, 503),
            "subscription_sharing_user_unavailable" => ("Сведения об аккаунте ChatGPT временно недоступны. Подождите немного и нажмите «Повторить».",
                true, 503),
            "subscription_sharing_unsupported_capability" => ($"ChatGPT не поддерживает в этом запросе: {param ?? message ?? "часть запроса"}. " +
                                                              "Уберите это (например, вложение или силу рассуждения) или выберите другую модель.", false, 400),
            "subscription_sharing_route_not_supported" => ("OpenAI не принимает такой запрос по подписке ChatGPT. Обновите Sharp Agent " +
                                                           "или выберите другого провайдера в меню модели.", false, 403),
            InvalidUserCode => ("OpenAI не смог проверить ваш аккаунт ChatGPT, хотя вход действует. Повторите запрос чуть позже; " +
                                "если ошибка не пройдёт, проверьте подключение Sharp Agent в настройках ChatGPT.", false, 401),
            "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context" =>
                ("Разрешение Sharp Agent на подписку ChatGPT не подходит для этого запроса. Откройте настройки, выйдите из ChatGPT " +
                 "и войдите снова.", false, 403),
            _ => number switch
            {
                401 => ("OpenAI не принял вход в ChatGPT или разрешение на подписку. Откройте настройки, выйдите из ChatGPT и войдите снова, " +
                        "разрешив использование подписки.", false, 401),
                403 => ("OpenAI не пропустил запрос" + Detail(message) + ". Так бывает, если подписку нельзя использовать из вашего региона " +
                        "или рабочего пространства. Выберите другого провайдера в меню модели.", false, 403),
                429 => ("ChatGPT ограничил частоту запросов. Подождите минуту и нажмите «Повторить».", wait != null, 429),
                503 => ("Запросы по подписке ChatGPT сейчас недоступны. Подождите немного и нажмите «Повторить».", true, 503),
                >= 500 => ($"Сервер OpenAI временно недоступен (HTTP {number}). Нажмите «Повторить».", true, number.Value),
                null when code is null or "error" or "server_error" or "rate_limit_exceeded" =>
                    ("ChatGPT не смог ответить" + Detail(message) + ". Нажмите «Повторить».", true, 0),
                null => ("ChatGPT отклонил запрос" + Detail(message) + ". Измените запрос или выберите другую модель.", false, 0),
                _ => ($"ChatGPT ответил ошибкой (HTTP {number})" + Detail(message) + ". Нажмите «Повторить» или выберите другую модель.",
                    false, number.Value),
            },
        };
        return new ApiException(text + ApiException.Tail(number, code, requestId), status ?? (usual > 0 ? (HttpStatusCode)usual : null),
            retry, code, wait) { Parameter = param };
    }

    private static string Detail(string? message) =>
        message?.TrimEnd().TrimEnd('.') is { Length: > 0 } text ? ": " + ApiException.Truncate(text, 300) : "";

    public void Dispose() => _http.Dispose();
}
