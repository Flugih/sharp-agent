namespace SharpAgent.Api;

static class ChatCompletionStream
{
    public static async Task<ChatResult> ReadAsync(HttpResponseMessage response, string? requestId, Action<string?, bool> reportText,
        Action<ModelProgress> reportProgress, CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        StringBuilder content = new(), reasoning = new();
        var calls = new SortedDictionary<long, (StringBuilder Id, StringBuilder Name, StringBuilder Arguments)>();
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
            if (chunk["error"] is JsonObject) throw ApiException.FromResponse(response.StatusCode, payload, requestId);
            usage = Usage(chunk) ?? usage;
            if ((chunk["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice) continue;
            received = true;
            finish = Str(choice["finish_reason"]) ?? finish;
            if (choice["delta"] is not JsonObject delta) continue;
            bool hadCalls = calls.Count > 0;
            foreach (var call in (delta["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                long index = Long(call["index"]);
                if (!calls.TryGetValue(index, out var pending)) calls.Add(index, pending = (new(), new(), new()));
                pending.Id.Append(Str(call["id"]));
                if (call["function"] is not JsonObject function) continue;
                pending.Name.Append(Str(function["name"]));
                pending.Arguments.Append(Str(function["arguments"]));
            }
            var text = Str(delta["content"]) ?? Str(delta["refusal"]) ?? "";
            content.Append(text);
            if (text.Length > 0 || !hadCalls && calls.Count > 0) reportText(text, calls.Count > 0);
            var field = delta["reasoning_content"] != null ? "reasoning_content" : "reasoning";
            if (Str(delta[field]) is not { Length: > 0 } reason) continue;
            reasoningField ??= field;
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
        if (reasoningField != null) message[reasoningField] = reasoning.ToString();
        if (calls.Count > 0)
            message["tool_calls"] = new JsonArray([.. calls.Values.Select(call => new JsonObject
            {
                ["id"] = call.Id.ToString(), ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name.ToString(), ["arguments"] = call.Arguments.ToString() },
            })]);
        return new ChatResult(message, finish, usage, requestId);
    }

    internal static UsageInfo? Usage(JsonObject? data) =>
        (data?["usage"] as JsonObject ?? (data?["x_groq"] as JsonObject)?["usage"] as JsonObject) is { } usage
            ? new UsageInfo(Long(usage["prompt_tokens"]), Long(usage["completion_tokens"]))
            : null;
}
