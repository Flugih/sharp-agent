namespace SharpAgent.Api;

static class ChatCompletionStream
{
    sealed class PendingCall
    {
        public readonly StringBuilder Id = new(), Name = new(), Arguments = new();
        public JsonNode? ExtraContent;
    }

    // Gemini's OpenAI layer may open the answer with its thought summary wrapped in <thought> tags.
    internal sealed class ThoughtTags
    {
        const string Open = "<thought>", Close = "</thought>";
        readonly StringBuilder _held = new();
        int _state; // 0: the answer may still open with a thought, 1: inside the thought, 2: plain answer

        public (string Text, string Thought) Push(string text)
        {
            if (_state == 2) return (text, "");
            var s = _held.Append(text).ToString();
            _held.Clear();
            if (_state == 0)
            {
                var start = s.TrimStart();
                if (!start.StartsWith(Open, StringComparison.Ordinal))
                {
                    if (Open.StartsWith(start, StringComparison.Ordinal))
                    {
                        _held.Append(s);
                        return ("", "");
                    }
                    _state = 2;
                    return (s, "");
                }
                _state = 1;
                s = start[Open.Length..];
            }
            if (s.IndexOf(Close, StringComparison.Ordinal) is var end and >= 0)
            {
                _state = 2;
                return (s[(end + Close.Length)..].TrimStart(), s[..end]);
            }
            int keep = Enumerable.Range(1, Math.Min(Close.Length - 1, s.Length)).LastOrDefault(n => s.EndsWith(Close[..n], StringComparison.Ordinal));
            _held.Append(s[^keep..]);
            return ("", s[..^keep]);
        }

        public (string Text, string Thought) Flush() => _state == 1 ? ("", _held.ToString()) : (_held.ToString(), "");

        public static (string Text, string Thought) Split(string text)
        {
            var tags = new ThoughtTags();
            var (head, thought) = tags.Push(text);
            var (rest, restThought) = tags.Flush();
            return (head + rest, thought + restThought);
        }
    }

    public static async Task<ChatResult> ReadAsync(HttpResponseMessage response, string? requestId, Action<string?, bool> reportText,
        Action<ModelProgress> reportProgress, CancellationToken ct, Action reportActivity)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        StringBuilder content = new(), reasoning = new(), rawReasoning = new();
        var calls = new SortedDictionary<long, PendingCall>();
        var details = new JsonArray();
        var tags = new ThoughtTags();
        var reasoningClock = Stopwatch.StartNew();
        string? finish = null, reasoningField = null;
        UsageInfo? usage = null;
        bool received = false, done = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            reportActivity();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (done = payload == "[DONE]") break;
            var chunk = ParseObject(payload) ?? throw new ApiException("Сервер прислал повреждённую часть ответа. Нажмите «Повторить».", retryable: true);
            if (chunk["error"] is JsonObject error)
                throw ApiException.FromResponse(Long(error["code"]) is >= 400 and <= 599 and var code ? (HttpStatusCode)code : response.StatusCode, payload, requestId);
            usage = Usage(chunk) ?? usage;
            if ((chunk["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice) continue;
            received = true;
            if (Str(choice["finish_reason"]) is { Length: > 0 } reasonForFinish) finish = reasonForFinish;
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
            var raw = AgentRunner.ExtractText(delta["content"]) ?? Str(delta["refusal"]) ?? "";
            var (text, thought) = Bool(((delta["extra_content"] as JsonObject)?["google"] as JsonObject)?["thought"]) == true ? ("", raw) : tags.Push(raw);
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
            else reason = ThinkingOf(delta["content"]) ?? DetailText(updates) ?? thought;
            if (string.IsNullOrEmpty(reason)) continue;
            reasoning.Append(reason);
            if (reasoning.Length == reason.Length || reasoningClock.ElapsedMilliseconds >= 200)
            {
                reportProgress(new(Reasoning: reasoning.ToString()));
                reasoningClock.Restart();
            }
        }
        if (!received || finish == null && (!done || calls.Count > 0))
            throw new ApiException("Связь с моделью оборвалась до конца ответа. Нажмите «Повторить».", retryable: true)
            { ConnectionInterrupted = true };
        var (restText, restThought) = tags.Flush();
        content.Append(restText);
        reasoning.Append(restThought);
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
            ? new UsageInfo(Long(usage["prompt_tokens"]), Long(usage["completion_tokens"]),
                Long((usage["prompt_tokens_details"] as JsonObject)?["cached_tokens"]), Long((usage["completion_tokens_details"] as JsonObject)?["reasoning_tokens"]))
            : null;
}
