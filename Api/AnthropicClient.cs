namespace SharpAgent.Api;

internal sealed class AnthropicClient(string apiKey, string baseUrl = AnthropicClient.BaseUrl) : IChatProvider
{
    internal const string BaseUrl = "https://api.anthropic.com/v1";
    readonly HttpClient _http = AppNetwork.Client(Timeout.InfiniteTimeSpan, redirects: false, cookies: false,
        headers: headers => headers.Add("anthropic-version", "2023-06-01"));
    Dictionary<string, (int MaxTokens, bool Adaptive)> _modelLimits = new(StringComparer.Ordinal);

    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        List<ModelInfo> models = [];
        Dictionary<string, (int, bool)> limits = new(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var query = "/models?limit=1000" + (cursor == null ? "" : "&after_id=" + Uri.EscapeDataString(cursor));
            using var response = await SendAsync(HttpMethod.Get, query, null, ct);
            var root = ParseObject(await response.Content.ReadAsStringAsync(ct));
            if (root?["data"] is not JsonArray data) throw new ApiException(Tr("Claude API не вернул список моделей.", "Claude API did not return a model list."));
            foreach (var item in data.OfType<JsonObject>())
            {
                if (Str(item["id"]) is not { Length: > 0 } id || Str(item["lifecycle"]) == "retired") continue;
                var caps = item["capabilities"] as JsonObject;
                var effort = caps?["effort"] as JsonObject;
                var levels = AppConfig.EffortLevels.Where(level => Bool(effort?[level]?["supported"]) == true).ToArray();
                var context = Long(item["max_input_tokens"]);
                if (!limits.TryAdd(id, ((int)Math.Clamp(Long(item["max_tokens"]), 0, int.MaxValue),
                    Bool(caps?["thinking"]?["types"]?["adaptive"]?["supported"]) == true))) continue;
                models.Add(new(id, context > 0 ? context : null, Bool(caps?["image_input"]?["supported"]), true, false, levels, Str(item["display_name"])));
            }
            if (Bool(root["has_more"]) != true) break;
            var next = Str(root["last_id"]);
            if (string.IsNullOrEmpty(next) || next == cursor) throw new ApiException(Tr("Claude API вернул неполный список моделей.", "Claude API returned an incomplete model list."));
            cursor = next;
        } while (true);
        _modelLimits = limits;
        return models;
    }

    public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools, Action<int, int>? retrying)
    {
        var schemas = ToolSchemas.Get(tools);
        var body = ToolSchemaWire.Serialize(Request(model, messages, schemas != null, maxTokens, effort, callTools), schemas);
        void Reset()
        {
            reportText(null, false);
            reportProgress(new());
        }
        return ApiException.RetryAsync(async (token, reportActivity) =>
        {
            using var response = await SendAsync(HttpMethod.Post, "/messages", body, token);
            var requestId = ApiException.Header(response, "request-id");
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                return await ReadStreamAsync(response, requestId, reportText, reportProgress, token, reportActivity);
            var answer = ParseObject(await response.Content.ReadAsStringAsync(token));
            if (answer?["content"] is not JsonArray content) throw BrokenResponse();
            return Finish(content, Str(answer["stop_reason"]), MergeUsage(answer["usage"] as JsonObject, new(0, 0)), requestId ?? Str(answer["id"]));
        }, _ => false, Reset, retrying, ct);
    }

    static readonly ToolSchemaWire ToolSchemas = new(static tools => new JsonArray([..
        tools.OfType<JsonObject>().Select(tool => tool["function"]).OfType<JsonObject>().Select(function => new JsonObject
        {
            ["name"] = function["name"]?.DeepClone(), ["description"] = function["description"]?.DeepClone(),
            ["input_schema"] = function["parameters"]?.DeepClone(),
        })]));

    JsonObject Request(string model, IReadOnlyList<JsonNode> messages, bool hasTools, int? maxTokens, string? effort, bool callTools)
    {
        var (limit, adaptive) = _modelLimits.GetValueOrDefault(model);
        var turns = new JsonArray();
        List<string> system = [];
        foreach (var message in messages.OfType<JsonObject>())
        {
            var role = Str(message["role"]);
            if (role is "system" or "developer")
            {
                if (AgentRunner.ExtractText(message["content"]) is { Length: > 0 } text) system.Add(text);
                continue;
            }
            var content = Content(message, role);
            if (content.Count == 0) continue;
            var targetRole = role == "assistant" ? "assistant" : "user";
            if (turns.LastOrDefault() is JsonObject last && Str(last["role"]) == targetRole && last["content"] is JsonArray blocks)
            {
                foreach (var block in content) blocks.Add(block?.DeepClone());
            }
            else turns.Add(new JsonObject { ["role"] = targetRole, ["content"] = content });
        }
        var body = new JsonObject
        {
            ["model"] = model, ["messages"] = turns, ["stream"] = true,
            ["max_tokens"] = Math.Min(maxTokens is > 0 ? maxTokens.Value : 16_384, limit > 0 ? limit : int.MaxValue),
        };
        // A stable explicit Anthropic cache boundary saves repeated system-token work.
        // Tool results and dynamic context are intentionally not pinned here.
        if (system.Count > 0) body["system"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = string.Join("\n\n", system),
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
        });
        if (hasTools)
        {
            body["tools"] = null;
            if (!callTools) body["tool_choice"] = new JsonObject { ["type"] = "none" };
        }
        if (adaptive) body["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
        if (effort != null) body["output_config"] = new JsonObject { ["effort"] = effort };
        return body;
    }

    static JsonArray Content(JsonObject message, string? role)
    {
        if (role == "tool")
            return [new JsonObject
            {
                ["type"] = "tool_result", ["tool_use_id"] = message["tool_call_id"]?.DeepClone(),
                ["content"] = AgentRunner.ExtractText(message["content"]) ?? "",
            }];
        if (role == "assistant" && message["reasoning_details"] is JsonArray { Count: > 0 } details &&
            details[0] is JsonObject replay && replay["claude_content"] is JsonArray original)
            return (JsonArray)original.DeepClone();
        var blocks = new JsonArray();
        void AddText(string? text)
        {
            if (!string.IsNullOrEmpty(text)) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        if (role == "user" && message["content"] is JsonArray parts)
        {
            foreach (var part in parts.OfType<JsonObject>())
            {
                if (Str(part["type"]) == "image_url" && Str((part["image_url"] as JsonObject)?["url"]) is { } url && ImageSource(url) is { } source)
                    blocks.Add(new JsonObject { ["type"] = "image", ["source"] = source });
                else AddText(Str(part["text"]));
            }
        }
        else AddText(AgentRunner.ExtractText(message["content"]));
        if (role == "assistant")
            foreach (var call in (message["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? [])
                if (call["function"] is JsonObject function)
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use", ["id"] = call["id"]?.DeepClone(), ["name"] = function["name"]?.DeepClone(),
                        ["input"] = ParseObject(Str(function["arguments"]) ?? "") ?? new JsonObject(),
                    });
        return blocks;
    }

    static JsonObject? ImageSource(string url)
    {
        if (url.StartsWith("data:image/", StringComparison.Ordinal) && url.IndexOf(";base64,", StringComparison.Ordinal) is > 5 and var split)
            return new() { ["type"] = "base64", ["media_type"] = url[5..split], ["data"] = url[(split + ";base64,".Length)..] };
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? new() { ["type"] = "url", ["url"] = url } : null;
    }

    async Task<ChatResult> ReadStreamAsync(HttpResponseMessage response, string? requestId, Action<string?, bool> reportText,
        Action<ModelProgress> reportProgress, CancellationToken ct, Action reportActivity)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        SortedDictionary<long, (JsonObject Block, StringBuilder Fragments)> blocks = [];
        var thoughts = new StringBuilder();
        var clock = Stopwatch.StartNew();
        UsageInfo usage = new(0, 0);
        string? stop = null;
        bool calling = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            reportActivity();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = ParseObject(line[5..]) ?? throw BrokenResponse();
            var index = Long(data["index"]);
            switch (Str(data["type"]))
            {
                case "message_start" when data["message"] is JsonObject message:
                    requestId ??= Str(message["id"]);
                    usage = MergeUsage(message["usage"] as JsonObject, usage);
                    break;
                case "content_block_start" when data["content_block"] is JsonObject block:
                    blocks[index] = ((JsonObject)block.DeepClone(), new());
                    var type = Str(block["type"]);
                    if (type == "tool_use" && !calling) reportText("", calling = true);
                    if (type == "text" && Str(block["text"]) is { Length: > 0 } first) reportText(first, calling);
                    if (type == "thinking") thoughts.Append(Str(block["thinking"]));
                    break;
                case "content_block_delta" when data["delta"] is JsonObject delta && blocks.TryGetValue(index, out var pending):
                    switch (Str(delta["type"]))
                    {
                        case "text_delta" when Str(delta["text"]) is { Length: > 0 } text:
                            pending.Fragments.Append(text);
                            reportText(text, calling);
                            break;
                        case "thinking_delta" when Str(delta["thinking"]) is { Length: > 0 } thinking:
                            pending.Fragments.Append(thinking);
                            thoughts.Append(thinking);
                            if (thoughts.Length == thinking.Length || clock.ElapsedMilliseconds >= 200)
                            {
                                reportProgress(new(Reasoning: thoughts.ToString()));
                                clock.Restart();
                            }
                            break;
                        case "signature_delta":
                            pending.Block["signature"] = Str(pending.Block["signature"]) + Str(delta["signature"]);
                            break;
                        case "input_json_delta":
                            pending.Fragments.Append(Str(delta["partial_json"]));
                            break;
                    }
                    break;
                case "message_delta":
                    stop = Str(data["delta"]?["stop_reason"]) ?? stop;
                    usage = MergeUsage(data["usage"] as JsonObject, usage);
                    break;
                case "message_stop" when stop != null:
                    return Finish([.. blocks.Values.Select(Materialize)], stop, usage, requestId);
                case "error":
                    throw Error(data, null, requestId, null);
            }
        }
        throw BrokenResponse();
    }

    static JsonObject Materialize((JsonObject Block, StringBuilder Fragments) pending)
    {
        var (block, fragments) = pending;
        if (fragments.Length == 0) return block;
        var text = fragments.ToString();
        switch (Str(block["type"]))
        {
            case "text":
                block["text"] = Str(block["text"]) + text;
                break;
            case "thinking":
                block["thinking"] = Str(block["thinking"]) + text;
                break;
            case "tool_use":
                block["input"] = (JsonNode?)ParseObject(text) ?? JsonValue.Create(text);
                break;
        }
        return block;
    }

    static ChatResult Finish(JsonArray blocks, string? stop, UsageInfo usage, string? requestId)
    {
        var content = blocks.OfType<JsonObject>().ToArray();
        var text = string.Concat(content.Where(block => Str(block["type"]) == "text").Select(block => Str(block["text"])));
        var thoughts = string.Concat(content.Where(block => Str(block["type"]) == "thinking").Select(block => Str(block["thinking"])));
        var message = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text : null };
        var calls = content.Where(block => Str(block["type"]) == "tool_use").ToArray();
        if (calls.Length > 0)
        {
            bool truncated = stop is "max_tokens" or "model_context_window_exceeded";
            bool complete = calls.All(call => call["input"] is JsonObject && Str(call["id"]) is { Length: > 0 } && Str(call["name"]) is { Length: > 0 });
            if (!complete && !truncated) throw BrokenResponse();
            message["tool_calls"] = new JsonArray([.. calls.Select(call => new JsonObject
            {
                ["id"] = call["id"]?.DeepClone(), ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call["name"]?.DeepClone(), ["arguments"] = Str(call["input"]) ?? "{}" },
            })]);
            if (complete && !truncated) message["reasoning_details"] = new JsonArray(new JsonObject { ["claude_content"] = blocks });
        }
        var finish = stop switch
        {
            "max_tokens" or "model_context_window_exceeded" => "length",
            "refusal" => "content_filter",
            _ => calls.Length > 0 ? "tool_calls" : "stop",
        };
        return new(message, finish, usage, requestId, thoughts.Length > 0 ? new(Reasoning: thoughts) : null);
    }

    static UsageInfo MergeUsage(JsonObject? usage, UsageInfo previous) => new(
        usage?["input_tokens"] != null ? Long(usage["input_tokens"]) + Long(usage["cache_creation_input_tokens"]) + Long(usage["cache_read_input_tokens"]) : previous.PromptTokens,
        usage?["output_tokens"] != null ? Long(usage["output_tokens"]) : previous.CompletionTokens,
        usage?["cache_read_input_tokens"] != null ? Long(usage["cache_read_input_tokens"]) : previous.CachedTokens);

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        if (apiKey.Length == 0 || apiKey.Any(c => c <= ' ' || c >= '\u007f'))
            throw new ApiException(Tr("Проверьте формат ключа Claude API в настройках.", "Check the Claude API key format in settings."), HttpStatusCode.Unauthorized);
        using var request = new HttpRequestMessage(method, baseUrl + path);
        request.Headers.Add("x-api-key", apiKey);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return response;
        using (response)
            throw Error(ParseObject(await response.Content.ReadAsStringAsync(ct)), response.StatusCode,
                ApiException.Header(response, "request-id"), response.Headers.RetryAfter?.Delta);
    }

    ApiException Error(JsonObject? body, HttpStatusCode? status, string? requestId, TimeSpan? wait)
    {
        var error = body?["error"] as JsonObject;
        var code = Str(error?["type"]);
        var message = status == HttpStatusCode.Unauthorized || code == "authentication_error"
            ? Tr("Claude API не принял ключ. Проверьте его в настройках.", "Claude API rejected the key. Check it in settings.")
            : Str(error?["message"]) ?? Tr("Claude API вернул ошибку.", "Claude API returned an error.");
        var retry = (int?)status >= 500 || status == HttpStatusCode.TooManyRequests && wait != null ||
            code is "overloaded_error" or "api_error" or "timeout_error";
        string Clean(string text) => text.Replace(apiKey, "***", StringComparison.Ordinal);
        return new(Clean(message + ApiException.Tail((int?)status, code, requestId ?? Str(body?["request_id"]))), status, retry,
            code == null ? null : Clean(code), wait);
    }

    static ApiException BrokenResponse() => new(Tr("Ответ Claude API оборвался или повреждён. Нажмите «Повторить».",
        "The Claude API response was incomplete or invalid. Select Retry."), retryable: true);

    public void Dispose() => _http.Dispose();
}
