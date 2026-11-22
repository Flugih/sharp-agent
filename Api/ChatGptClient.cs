namespace SharpAgent.Api;

public sealed class ChatGptClient(ChatGptAuth auth) : OpenAiResponsesClient(auth);

public class OpenAiResponsesClient : IChatProvider
{
    public const string UsageUrl = "https://chatgpt.com/settings/usage", BaseUrl = "https://api.openai.com/v1";
    internal const string LimitCode = "subscription_sharing_usage_limit_exceeded", InvalidUserCode = "subscription_sharing_invalid_user";
    const string Namespace = "functions", IneligibleCode = "subscription_sharing_user_not_eligible";
    static readonly string[] Gpt6Efforts = ["low", "medium", "high", "xhigh", "max"];
    readonly HashSet<string> _noSummaries = new(StringComparer.OrdinalIgnoreCase), _noStreaming = new(StringComparer.OrdinalIgnoreCase);
    readonly ChatGptAuth? _auth;
    readonly string? _apiKey;
    readonly string _baseUrl;
    readonly HttpClient _http;

    protected OpenAiResponsesClient(ChatGptAuth auth)
    {
        _auth = auth;
        _baseUrl = BaseUrl;
        _http = AppNetwork.Client(Timeout.InfiniteTimeSpan, headers: headers => headers.UserAgent.ParseAdd(ChatGptAuth.UserAgent));
    }

    internal OpenAiResponsesClient(string apiKey, string baseUrl = BaseUrl)
    {
        _apiKey = apiKey;
        _baseUrl = baseUrl;
        _http = AppNetwork.Client(Timeout.InfiniteTimeSpan, headers: headers => headers.UserAgent.Add(new("SharpAgent", "1.0")));
    }

    public event Action? LimitsChanged;
    public string? LimitMessage { get; private set; }

    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        using var response = await SendAsync(() => new(HttpMethod.Get, _baseUrl + "/models"), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw Explain(response, text);
        var root = ParseObject(text) ?? throw new ApiException(_auth != null ? "ChatGPT вернул не JSON: " + ApiException.Truncate(text, 200)
            : "OpenAI API вернул ответ в неизвестном формате.");
        if (_auth == null)
        {
            if (root["data"] is not JsonArray data) throw new ApiException("OpenAI API не вернул список моделей.");
            return data.OfType<JsonObject>().Select(item => Str(item["id"])).OfType<string>().Where(IsApiChatModel)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(ApiModelInfo).ToList();
        }
        List<ModelInfo> models = [];
        foreach (var item in (root["models"] as JsonArray)?.OfType<JsonObject>() ?? [])
            if (Str(item["visibility"]) == "list" && Str(item["slug"]) is { Length: > 0 } slug)
                models.Add(new(slug, Long(item["context_window"]) is > 0 and var context ? context : null,
                    item["input_modalities"] is JsonArray kinds ? kinds.Any(kind => Str(kind) == "image") : null, true, false,
                    (item["supported_reasoning_levels"] as JsonArray)?.Select(level => Str(level is JsonObject entry ? entry["effort"] : level) ?? "")
                        .Where(AppConfig.EffortLevels.Contains).Distinct().ToArray() ?? [], Str(item["display_name"])));
        return models;
    }

    static bool IsApiChatModel(string id) => Regex.IsMatch(id, @"^(?:gpt-(?:4o|4\.1|5(?:\.\d+)?|6(?:\.\d+)?)|o[134])(?:-|$)") &&
        !Regex.IsMatch(id, @"-(?:audio|realtime|transcribe|tts|image|search|cyber|bio|chat|preview)(?:-|$)");

    static bool IsGpt6ReasoningModel(string id) => Regex.IsMatch(id, @"^gpt-6(?:\.\d+)?-(?:astra|sol|terra|luna)(?:-|$)");

    static ModelInfo ApiModelInfo(string id) => new(id, null, null, true, false, IsGpt6ReasoningModel(id) ? Gpt6Efforts : []);

    public async Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools, Action<int, int>? retrying)
    {
        bool summary = (_auth != null || IsGpt6ReasoningModel(model)) && !_noSummaries.Contains(model), streaming = !_noStreaming.Contains(model);
        var body = Request(model, messages, tools, maxTokens, effort, summary);
        if (!callTools && (_auth != null || tools is { Count: > 0 })) body["tool_choice"] = "none";
        void Reset()
        {
            reportText(null, false);
            reportProgress(new());
        }
        var result = await ApiException.RetryAsync((attemptToken, reportActivity) =>
        {
            body["stream"] = streaming;
            return SendCompletionAsync(body.ToJsonString(), streaming, reportText, reportProgress, attemptToken, reportActivity);
        }, ex =>
        {
            if (summary && SummaryRejected(ex, effort != null))
            {
                _noSummaries.Add(model);
                summary = false;
                if (body["reasoning"] is JsonObject reasoning) reasoning.Remove("summary");
                if (effort == null) body.Remove("reasoning");
            }
            else if (streaming && ex.RejectsStream())
            {
                _noStreaming.Add(model);
                streaming = false;
            }
            else
            {
                if (_auth != null && ex.Code is (LimitCode or IneligibleCode)) SetLimit(ex.Message.Split('\n')[0]);
                else if (ex.Code == "content_filter") Reset();
                else return false;
                return null;
            }
            Reset();
            return true;
        }, Reset, retrying, ct);
        SetLimit(null);
        return result;
    }

    static bool SummaryRejected(ApiException ex, bool hasEffort) =>
        ex.StatusCode is null or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity &&
        (ex.Parameter == "reasoning.summary" || !hasEffort && ex.Parameter == "reasoning" ||
         ex.Message.Contains("reasoning.summary", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("reasoning summar", StringComparison.OrdinalIgnoreCase));

    JsonObject Request(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort, bool summary)
    {
        JsonArray functions =
        [
            .. tools?.OfType<JsonObject>().Select(tool => tool["function"]).OfType<JsonObject>().Select(function => new JsonObject
            {
                ["type"] = "function", ["name"] = function["name"]?.DeepClone(), ["description"] = function["description"]?.DeepClone(),
                ["strict"] = false, ["parameters"] = function["parameters"]?.DeepClone(),
            }) ?? [],
        ];
        JsonArray input = _auth != null && functions.Count > 0
            ?
            [
                new JsonObject
                {
                    ["type"] = "additional_tools", ["role"] = "developer",
                    ["tools"] = new JsonArray(new JsonObject { ["type"] = "namespace", ["name"] = Namespace, ["description"] = "", ["tools"] = functions }),
                },
            ]
            : [];
        foreach (var message in messages.OfType<JsonObject>()) AddInput(input, message);
        var body = new JsonObject { ["model"] = model, ["input"] = input, ["store"] = false };
        if (_auth == null && functions.Count > 0) body["tools"] = functions;
        if (maxTokens is > 0) body["max_output_tokens"] = Math.Max(16, maxTokens.Value);
        var reasoning = new JsonObject();
        if (effort != null) reasoning["effort"] = effort;
        if (summary) reasoning["summary"] = "auto";
        if (reasoning.Count > 0) body["reasoning"] = reasoning;
        return body;
    }

    void AddInput(JsonArray input, JsonObject message)
    {
        var text = Text(message["content"]);
        switch (Str(message["role"]))
        {
            case "system" or "developer":
                input.Add(Message("developer", "input_text", text));
                break;
            case "user":
                input.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = UserContent(message["content"]) });
                break;
            case "assistant":
                if (_auth == null && message["reasoning_details"] is JsonArray reasoning)
                    foreach (var item in reasoning) input.Add(item?.DeepClone());
                if (Str(message["commentary"]) is { Length: > 0 } commentary) input.Add(Message("assistant", "output_text", commentary, "commentary"));
                if (!string.IsNullOrEmpty(text)) input.Add(Message("assistant", "output_text", text, Str(message["phase"])));
                foreach (var call in (message["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var function = call["function"] as JsonObject;
                    var functionCall = new JsonObject
                    {
                        ["type"] = "function_call", ["name"] = Str(function?["name"]) ?? "",
                        ["arguments"] = Str(function?["arguments"]) ?? "{}", ["call_id"] = Str(call["id"]) ?? "",
                    };
                    if (_auth != null) functionCall["namespace"] = Namespace;
                    input.Add(functionCall);
                }
                break;
            case "tool":
                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = Str(message["tool_call_id"]) ?? "", ["output"] = text ?? "" });
                break;
        }
    }

    static JsonArray UserContent(JsonNode? content)
    {
        if (content is not JsonArray parts) return [Part("input_text", Str(content))];
        var result = new JsonArray();
        foreach (var part in parts.OfType<JsonObject>())
        {
            if (Str(part["type"]) == "image_url")
                result.Add(new JsonObject
                {
                    ["type"] = "input_image", ["image_url"] = Str((part["image_url"] as JsonObject)?["url"]) ?? Str(part["image_url"]),
                });
            else if (Str(part["text"]) is { } text) result.Add(Part("input_text", text));
        }
        return result;
    }

    static JsonObject Part(string type, string? text) => new() { ["type"] = type, ["text"] = text ?? "" };

    static JsonObject Message(string role, string type, string? text, string? phase = null)
    {
        var message = new JsonObject { ["type"] = "message", ["role"] = role, ["content"] = new JsonArray(Part(type, text)) };
        if (phase is "commentary" or "final_answer") message["phase"] = phase;
        return message;
    }

    static string? Text(JsonNode? content) => content is JsonArray parts
        ? string.Concat(parts.OfType<JsonObject>().Select(part => Str(part["text"]) ?? Str(part["refusal"])))
        : Str(content);

    async Task<ChatResult> SendCompletionAsync(string body, bool streaming, Action<string?, bool> reportText, Action<ModelProgress> reportProgress,
        CancellationToken ct, Action reportActivity)
    {
        using var response = await SendAsync(() => new(HttpMethod.Post, _baseUrl + "/responses")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            Headers = { Accept = { new(streaming ? "text/event-stream" : "application/json") } },
        }, ct);
        if (!response.IsSuccessStatusCode) throw Explain(response, await response.Content.ReadAsStringAsync(ct));
        var requestId = RequestId(response);
        if (!streaming || response.Content.Headers.ContentType?.MediaType == "application/json")
            return FinishResponse([], ParseObject(await response.Content.ReadAsStringAsync(ct))
                ?? throw new ApiException($"{ServiceName} вернул ответ в неизвестном формате. Нажмите «Повторить»."), requestId);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        List<JsonObject> items = [];
        var summaries = new SortedDictionary<(long Output, long Part), string>();
        HashSet<long> commentaryItems = [];
        List<string> commentary = [];
        void ReportProgress() => reportProgress(new(Join(summaries.Values), Join(commentary)));
        long? textItem = null;
        bool calls = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            reportActivity();
            if (!line.StartsWith("data:", StringComparison.Ordinal) || ParseObject(line[5..]) is not { } data) continue;
            var answer = data["response"] as JsonObject;
            var output = Long(data["output_index"]);
            var type = Str(data["type"]);
            switch (type)
            {
                case "response.output_item.added" when IsCommentary(data["item"] as JsonObject):
                    commentaryItems.Add(output);
                    break;
                case "response.output_text.delta" or "response.refusal.delta"
                    when Str(data["delta"]) is { Length: > 0 } delta && !commentaryItems.Contains(output):
                    reportText(textItem != null && textItem != output ? "\n\n" + delta : delta, calls);
                    textItem = output;
                    break;
                case "response.output_item.added" when Str((data["item"] as JsonObject)?["type"]) == "function_call":
                    calls = true;
                    reportText("", true);
                    break;
                case "response.reasoning_summary_text.done" when Str(data["text"]) is { } summary:
                    summaries[(output, Long(data["summary_index"]))] = summary;
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
                    if (Str(item["type"]) == "reasoning" && item["summary"] is JsonArray parts)
                    {
                        for (int i = 0; i < parts.Count; i++)
                            if (parts[i] is JsonObject part && Str(part["text"]) is { } text) summaries[(output, i)] = text;
                        ReportProgress();
                    }
                    break;
                case "response.completed" or "response.incomplete":
                    return FinishResponse(items, answer, requestId, summaries.Values, type == "response.incomplete");
                case "response.failed":
                    throw Failed(answer?["error"] as JsonObject, requestId);
                case "error":
                    throw Failed(data["error"] as JsonObject ?? data, requestId);
            }
        }
        throw new ApiException($"Связь с {ServiceName} оборвалась до конца ответа. Нажмите «Повторить».", retryable: true);
    }

    ChatResult FinishResponse(List<JsonObject> items, JsonObject? answer, string? requestId, IEnumerable<string>? summaries = null,
        bool incomplete = false)
    {
        var status = Str(answer?["status"]);
        var error = answer?["error"] as JsonObject;
        if (status == "failed" || error != null) throw Failed(error, requestId);
        var reason = Str((answer?["incomplete_details"] as JsonObject)?["reason"]);
        if (reason == "content_filter")
            return new(new JsonObject(), reason, answer?["usage"] is JsonObject billed
                ? ReadUsage(billed) : null, requestId);
        if (answer?["output"] is JsonArray { Count: > 0 } output) items = output.OfType<JsonObject>().ToList();
        List<string> texts = [], commentary = [];
        var calls = new JsonArray();
        string? phase = null;
        foreach (var item in items)
        {
            switch (Str(item["type"]))
            {
                case "message" when Text(item["content"]) is { Length: > 0 } text:
                    if (IsCommentary(item)) commentary.Add(text);
                    else
                    {
                        texts.Add(text);
                        phase ??= Str(item["phase"]);
                    }
                    break;
                case "function_call":
                    calls.Add(new JsonObject
                    {
                        ["id"] = Str(item["call_id"]) is { Length: > 0 } id ? id : "call_" + Guid.NewGuid().ToString("N")[..16], ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = Str(item["name"]) ?? "", ["arguments"] = Str(item["arguments"]) ?? "{}" },
                    });
                    break;
            }
        }
        var content = string.Join("\n\n", texts);
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content.Length > 0 ? content : null };
        if (calls.Count > 0) message["tool_calls"] = calls;
        if (_auth == null && calls.Count > 0)
        {
            JsonArray reasoning = [.. items.Where(item => Str(item["type"]) == "reasoning" && Str(item["encrypted_content"]) != null)
                .Select(item => item.DeepClone())];
            if (reasoning.Count > 0) message["reasoning_details"] = reasoning;
        }
        var finalSummaries = items.Where(item => Str(item["type"]) == "reasoning")
            .SelectMany(item => (item["summary"] as JsonArray)?.OfType<JsonObject>() ?? []).Select(part => Str(part["text"])).OfType<string>();
        var finish = incomplete || status == "incomplete" || reason != null ? "length" : calls.Count > 0 ? "tool_calls" : "stop";
        return new ChatResult(message, finish,
            answer?["usage"] is JsonObject usage ? ReadUsage(usage) : null,
            requestId ?? Str(answer?["id"]), new(Join(finalSummaries) ?? Join(summaries ?? []), Join(commentary)), phase);
    }

    static UsageInfo ReadUsage(JsonObject usage) => new(Long(usage["input_tokens"]), Long(usage["output_tokens"]),
        Long((usage["input_tokens_details"] as JsonObject)?["cached_tokens"]), Long((usage["output_tokens_details"] as JsonObject)?["reasoning_tokens"]));

    static bool IsCommentary(JsonObject? item) => Str(item?["type"]) == "message" && Str(item?["phase"]) == "commentary";

    static string? Join(IEnumerable<string> texts) =>
        string.Join("\n\n", texts.Where(text => !string.IsNullOrWhiteSpace(text))) is { Length: > 0 } joined ? joined : null;

    async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        if (_auth == null)
        {
            using var request = build();
            request.Headers.Authorization = new("Bearer", _apiKey);
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        if (_auth.Connected && !_auth.PlanAllowed)
            throw new ApiException("Sharp Agent не получил разрешения использовать подписку ChatGPT. Откройте настройки и нажмите «Разрешить использование подписки».",
                HttpStatusCode.Forbidden);
        for (bool recovered = false; ; recovered = true)
        {
            var token = await _auth.AccessAsync(ct);
            using var request = build();
            request.Headers.Authorization = new("Bearer", token);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || recovered) return response;
            response.Dispose();
            await _auth.RecoverAsync(token, ct);
        }
    }

    void SetLimit(string? text)
    {
        if (text == LimitMessage) return;
        LimitMessage = text;
        LimitsChanged?.Invoke();
    }

    static string? RequestId(HttpResponseMessage response) =>
        ApiException.Header(response, "x-request-id") ?? ApiException.Header(response, "openai-request-id");

    string ServiceName => _auth != null ? "ChatGPT" : "OpenAI API";

    ApiException Explain(HttpResponseMessage response, string text)
    {
        if (_auth == null)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new ApiException("OpenAI API не принял ключ. Проверьте его в настройках.", response.StatusCode);
            return ApiException.FromResponse(response.StatusCode, RedactKey(text), RequestId(response), response.Headers.RetryAfter?.Delta);
        }
        var root = ParseObject(text);
        var error = root?["error"] as JsonObject;
        var detail = root?["detail"];
        return Failure(response.StatusCode, Str(error?["code"]), Str(error?["param"]), Str(error?["message"]) ?? Str((detail as JsonObject)?["message"] ?? detail),
            RequestId(response), response.Headers.RetryAfter?.Delta);
    }

    ApiException Failed(JsonObject? error, string? requestId)
    {
        var code = Str(error?["code"]) ?? Str(error?["type"]);
        if (_auth != null) return Failure(null, code, Str(error?["param"]), Str(error?["message"]), requestId, null);
        var message = RedactKey(Str(error?["message"]) ?? "");
        return new ApiException("OpenAI API вернул ошибку" + Detail(message) + ApiException.Tail(null, code, requestId),
            retryable: code is "server_error" or "rate_limit_exceeded", code: code) { Parameter = Str(error?["param"]) };
    }

    string RedactKey(string text) => _apiKey is { Length: > 0 } key ? text.Replace(key, "***", StringComparison.Ordinal) : text;

    static ApiException Failure(HttpStatusCode? status, string? code, string? param, string? message, string? requestId, TimeSpan? wait)
    {
        int? number = (int?)status;
        var (text, retry, usual) = code switch
        {
            "context_length_exceeded" => ("Запрос не помещается в память модели (context_length_exceeded).", false, 400),
            IneligibleCode => ("Подписку ChatGPT этого аккаунта или рабочего пространства нельзя использовать в Sharp Agent. Выберите другого провайдера в меню модели.",
                false, 403),
            LimitCode => ("Достигнут лимит использования подписки ChatGPT — общий или заданный для Sharp Agent. Проверьте его в настройках ChatGPT («Управлять использованием») или выберите другого провайдера в меню модели.",
                false, 429),
            "subscription_sharing_usage_unavailable" => ("ChatGPT не смог проверить лимиты подписки. Подождите немного и нажмите «Повторить».", true, 503),
            "subscription_sharing_user_unavailable" => ("Сведения об аккаунте ChatGPT временно недоступны. Подождите немного и нажмите «Повторить».", true, 503),
            "subscription_sharing_unsupported_capability" => ($"ChatGPT не поддерживает в этом запросе: {param ?? message ?? "часть запроса"}. Уберите это (например, вложение или силу рассуждения) или выберите другую модель.",
                false, 400),
            "subscription_sharing_route_not_supported" => ("OpenAI не принимает такой запрос по подписке ChatGPT. Обновите Sharp Agent или выберите другого провайдера в меню модели.",
                false, 403),
            InvalidUserCode => ("OpenAI не смог проверить ваш аккаунт ChatGPT, хотя вход действует. Повторите запрос чуть позже; если ошибка не пройдёт, проверьте подключение Sharp Agent в настройках ChatGPT.",
                false, 401),
            "chatpass_v2_scope_not_authorized" or "chatpass_v2_invalid_authorization_context" =>
                ("Разрешение Sharp Agent на подписку ChatGPT не подходит для этого запроса. Откройте настройки, выйдите из ChatGPT и войдите снова.", false, 403),
            _ => number switch
            {
                401 => ("OpenAI не принял вход в ChatGPT или разрешение на подписку. Откройте настройки, выйдите из ChatGPT и войдите снова, разрешив использование подписки.",
                    false, 401),
                403 => ("OpenAI не пропустил запрос" + Detail(message) + ". Так бывает, если подписку нельзя использовать из вашего региона или рабочего пространства. Выберите другого провайдера в меню модели.",
                    false, 403),
                429 => ("ChatGPT ограничил частоту запросов. Подождите минуту и нажмите «Повторить».", wait != null, 429),
                503 => ("Запросы по подписке ChatGPT сейчас недоступны. Подождите немного и нажмите «Повторить».", true, 503),
                >= 500 => ($"Сервер OpenAI временно недоступен (HTTP {number}). Нажмите «Повторить».", true, number.Value),
                null when code is null or "error" or "server_error" or "rate_limit_exceeded" =>
                    ("ChatGPT не смог ответить" + Detail(message) + ". Нажмите «Повторить».", true, 0),
                null => ("ChatGPT отклонил запрос" + Detail(message) + ". Измените запрос или выберите другую модель.", false, 0),
                _ => ($"ChatGPT ответил ошибкой (HTTP {number})" + Detail(message) + ". Нажмите «Повторить» или выберите другую модель.", false, number.Value),
            },
        };
        return new ApiException(text + ApiException.Tail(number, code, requestId), status ?? (usual > 0 ? (HttpStatusCode)usual : null), retry, code, wait)
        {
            Parameter = param,
        };
    }

    static string Detail(string? message) => message?.TrimEnd().TrimEnd('.') is { Length: > 0 } text ? ": " + ApiException.Truncate(text, 300) : "";

    public void Dispose() => _http.Dispose();
}
