using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using SharpAgent.Core;

namespace SharpAgent.Api;

internal static class ChatCompletionStream
{
    private sealed class Call
    {
        private readonly StringBuilder _id = new(), _name = new(), _arguments = new();

        public void Append(JsonObject delta)
        {
            _id.Append(JsonUtil.Str(delta["id"]));
            if (delta["function"] is not JsonObject function) return;
            _name.Append(JsonUtil.Str(function["name"]));
            _arguments.Append(JsonUtil.Str(function["arguments"]));
        }

        public JsonObject ToJson() => new()
        {
            ["id"] = _id.ToString(),
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = _name.ToString(), ["arguments"] = _arguments.ToString() },
        };
    }

    public static async Task<ChatResult> ReadAsync(HttpResponseMessage response, string? requestId, Action<string?, bool> reportText,
        Action<ModelProgress> reportProgress, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<long, Call>();
        var reasoningClock = Stopwatch.StartNew();
        string? finish = null, reasoningField = null;
        UsageInfo? usage = null;
        bool received = false, done = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload == "[DONE]")
            {
                done = true;
                break;
            }
            if (JsonUtil.ParseObject(payload) is not { } chunk)
                throw new ApiException("Сервер прислал повреждённую часть ответа. Нажмите «Повторить».", retryable: true);
            if (chunk["error"] is JsonObject) throw ApiException.FromResponse(response.StatusCode, payload, requestId);
            usage = Usage(chunk) ?? usage;
            if ((chunk["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice) continue;
            received = true;
            finish = JsonUtil.Str(choice["finish_reason"]) ?? finish;
            if (choice["delta"] is not JsonObject delta) continue;

            bool hadCalls = calls.Count > 0;
            foreach (var call in (delta["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                long index = JsonUtil.Long(call["index"]);
                if (!calls.TryGetValue(index, out var pending)) calls.Add(index, pending = new Call());
                pending.Append(call);
            }
            var text = JsonUtil.Str(delta["content"]) ?? JsonUtil.Str(delta["refusal"]) ?? "";
            content.Append(text);
            if (text.Length > 0 || !hadCalls && calls.Count > 0) reportText(text, calls.Count > 0);

            var field = delta["reasoning_content"] != null ? "reasoning_content" : "reasoning";
            if (JsonUtil.Str(delta[field]) is not { Length: > 0 } reason) continue;
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
        if (calls.Count > 0) message["tool_calls"] = new JsonArray(calls.Values.Select(call => (JsonNode)call.ToJson()).ToArray());
        return new ChatResult(message, finish, usage, requestId);
    }

    internal static UsageInfo? Usage(JsonObject? data) =>
        (data?["usage"] as JsonObject ?? (data?["x_groq"] as JsonObject)?["usage"] as JsonObject) is { } usage
            ? new UsageInfo(JsonUtil.Long(usage["prompt_tokens"]), JsonUtil.Long(usage["completion_tokens"]))
            : null;
}
