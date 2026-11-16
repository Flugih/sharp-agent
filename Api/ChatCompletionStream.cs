namespace SharpAgent.Api;

static class ChatCompletionStream
{
    sealed class PendingCall
    {
        public readonly StringBuilder Id = new(), Name = new(), Arguments = new();
        public JsonNode? ExtraContent;
    }

    public static async Task<ChatResult> ReadAsync(HttpResponseMessage response, string? requestId, Action<string?, bool> reportText,
        Action<ModelProgress> reportProgress, CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        StringBuilder content = new(), reasoning = new(), rawReasoning = new();
        var calls = new SortedDictionary<long, PendingCall>();
        var details = new JsonArray();
        var reasoningClock = Stopwatch.StartNew();
        string? finish = null, reasoningField = null;
        UsageInfo? usage = null;
        bool received = false, done = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (done = payload == "[DONE]") break;
            var chunk = ParseObject(payload) ?? throw new ApiException("Сервер прислал повреждённую часть ответа. Нажмите «Повторить».", retryable: true);
            if (chunk["error"] is JsonObject error)
                throw ApiException.FromResponse(Long(error["code"]) is >= 400 and <= 599 and var code ? (HttpStatusCode)code : response.StatusCode, payload, requestId);
            usage = Usage(chunk) ?? usage;
            if ((chunk["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice) continue;
            received = true;
            finish = Str(choice["finish_reason"]) ?? finish;
            if (choice["delta"] is not JsonObject delta) continue;
            bool hadCalls = calls.Count > 0;
            int position = 0;
            foreach (var call in (delta["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                long index = call["index"] != null ? Long(call["index"]) : position;
                position++;
                if (!calls.TryGetValue(index, out var pending)) calls.Add(index, pending = new());
                pending.Id.Append(Str(call["id"]));
                if (call["extra_content"] is { } extra) pending.ExtraContent = extra.DeepClone();
                if (call["function"] is not JsonObject function) continue;
                pending.Name.Append(Str(function["name"]));
                pending.Arguments.Append(Str(function["arguments"]));
            }
            var text = AgentRunner.ExtractText(delta["content"]) ?? Str(delta["refusal"]) ?? "";
            content.Append(text);
            if (text.Length > 0 || !hadCalls && calls.Count > 0) reportText(text, calls.Count > 0);
            var updates = delta["reasoning_details"] as JsonArray;
            MergeDetails(details, updates);
            var field = delta["reasoning_content"] != null ? "reasoning_content" : "reasoning";
            var reason = Str(delta[field]);
            if (!string.IsNullOrEmpty(reason))
            {
                reasoningField ??= field;
                rawReasoning.Append(reason);
            }
            else reason = ThinkingOf(delta["content"]) ?? DetailText(updates);
            if (string.IsNullOrEmpty(reason)) continue;
            reasoning.Append(reason);
            if (reasoning.Length == reason.Length || reasoningClock.ElapsedMilliseconds >= 200)
            {
                reportProgress(new(Reasoning: reasoning.ToString()));
                reasoningClock.Restart();
            }
        }
        if (!received || finish == null && (!done || calls.Count > 0))
            throw new ApiException("Связь с моделью оборвалась до конца ответа. Нажмите «Повторить».", retryable: true);
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content.Length > 0 ? content.ToString() : null };
        if (reasoningField != null) message[reasoningField] = rawReasoning.ToString();
        if (details.Count > 0) message["reasoning_details"] = details;
        if (calls.Count > 0)
            message["tool_calls"] = new JsonArray([.. calls.Values.Select(call =>
            {
                var entry = new JsonObject
                {
                    ["id"] = call.Id.ToString(), ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name.ToString(), ["arguments"] = call.Arguments.ToString() },
                };
                if (call.ExtraContent != null) entry["extra_content"] = call.ExtraContent;
                return entry;
            })]);
        return new ChatResult(message, finish, usage, requestId,
            reasoningField == null && reasoning.Length > 0 && details.Count == 0 ? new(Reasoning: reasoning.ToString()) : null);
    }

    internal static string? ThinkingOf(JsonNode? content) => content is JsonArray parts
        ? string.Concat(parts.OfType<JsonObject>().Where(part => Str(part["type"]) == "thinking")
            .Select(part => AgentRunner.ExtractText(part["thinking"]))) is { Length: > 0 } text ? text : null : null;

    internal static string? DetailText(JsonArray? details) => details == null ? null :
        string.Concat(details.OfType<JsonObject>().Select(part => Str(part["text"]) ?? Str(part["summary"]))) is { Length: > 0 } text ? text : null;

    static void MergeDetails(JsonArray details, JsonArray? updates)
    {
        foreach (var update in updates?.OfType<JsonObject>() ?? [])
        {
            var target = update["index"] == null ? null : details.OfType<JsonObject>().FirstOrDefault(part =>
                Str(part["index"]) == Str(update["index"]) && Str(part["type"]) == Str(update["type"]) &&
                (part["id"] == null || update["id"] == null || Str(part["id"]) == Str(update["id"])));
            if (target == null)
            {
                details.Add(update.DeepClone());
                continue;
            }
            foreach (var (key, value) in update)
                if (key is "text" or "summary" or "data" or "signature" && Str(value) is { } fragment)
                    target[key] = Str(target[key]) + fragment;
                else if (value != null) target[key] = value.DeepClone();
        }
    }

    internal static UsageInfo? Usage(JsonObject? data) =>
        (data?["usage"] as JsonObject ?? (data?["x_groq"] as JsonObject)?["usage"] as JsonObject) is { } usage
            ? new UsageInfo(Long(usage["prompt_tokens"]), Long(usage["completion_tokens"]))
            : null;
}
