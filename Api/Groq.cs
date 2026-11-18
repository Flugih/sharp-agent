namespace SharpAgent.Api;

public sealed record GroqLimits(long? Requests, long? RequestsLeft, DateTime? RequestsReset, long? Tokens, long? TokensLeft, DateTime? TokensReset,
    DateTime Updated);

public static partial class Groq
{
    public const string BaseUrl = "https://api.groq.com/openai/v1";
    public const string KeysUrl = "https://console.groq.com/keys";
    static readonly string[] NonChat = ["whisper", "orpheus", "tts", "guard"], Fields = ["role", "content", "tool_calls", "tool_call_id"];
    static readonly HttpClient Speech = AppNetwork.Client(TimeSpan.FromMinutes(5));

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(ms|h|m|s)?", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPart();

    [GeneratedRegex(@"Limit (\d+).*?Requested (\d+)", RegexOptions.Singleline)]
    private static partial Regex RequestSize();

    public static bool IsChatModel(string id, JsonObject? model) =>
        Bool(model?["active"]) != false && !NonChat.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase));

    public static string[] EffortsOf(string id) =>
        id.StartsWith("openai/gpt-oss-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("qwen/qwen3", StringComparison.OrdinalIgnoreCase)
            ? ["low", "medium", "high"]
            : id.Contains("muse-spark", StringComparison.OrdinalIgnoreCase) ? ["low", "medium", "high", "xhigh"] : [];

    public static IEnumerable<JsonNode> Clean(IReadOnlyList<JsonNode> messages) => messages.Select(node =>
    {
        if (node is not JsonObject message) return node.DeepClone();
        var clean = new JsonObject();
        foreach (var field in Fields)
            if (message.TryGetPropertyValue(field, out var value))
                clean[field] = field == "tool_calls" && value is JsonArray calls
                    ? new JsonArray([.. calls.OfType<JsonObject>().Select(call => new JsonObject
                    {
                        ["id"] = Str(call["id"]), ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = Str((call["function"] as JsonObject)?["name"]), ["arguments"] = Str((call["function"] as JsonObject)?["arguments"]) ?? "{}",
                        },
                    })])
                    : value?.DeepClone();
        return clean;
    });

    public static GroqLimits? LimitsFrom(HttpResponseMessage response)
    {
        string? Get(string name) => ApiException.Header(response, "x-ratelimit-" + name);
        long? Number(string name) => long.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        var now = DateTime.Now;
        DateTime? Reset(string name) => Duration(Get("reset-" + name)) is { } wait ? now + wait : null;
        var limits = new GroqLimits(Number("limit-requests"), Number("remaining-requests"), Reset("requests"), Number("limit-tokens"),
            Number("remaining-tokens"), Reset("tokens"), now);
        return limits.Requests == null && limits.Tokens == null ? null : limits;
    }

    public static async Task<string> TranscribeAsync(string apiKey, byte[] wav, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/audio/transcriptions")
        {
            Content = new MultipartFormDataContent
            {
                { new StringContent("whisper-large-v3-turbo"), "model" },
                { new StringContent("verbose_json"), "response_format" },
                { new ByteArrayContent(wav) { Headers = { ContentType = new("audio/wav") } }, "file", "voice.wav" },
            },
            Headers = { Authorization = new("Bearer", apiKey), UserAgent = { new("SharpAgent", "1.0") } },
        };
        using var response = await Speech.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var wait = response.Headers.RetryAfter?.Delta;
            throw Explain(response.StatusCode, body, wait, speech: true) ?? ApiException.FromResponse(response.StatusCode, body, null, wait);
        }
        var root = ParseObject(body) ?? throw new ApiException("Groq вернул не JSON: " + ApiException.Truncate(body, 200));
        IEnumerable<string?> parts = root["segments"] is JsonArray { Count: > 0 } segments
            ? segments.OfType<JsonObject>().Where(segment => !(Double(segment["no_speech_prob"]) > 0.6 && Double(segment["avg_logprob"]) < -1))
                .Select(segment => Str(segment["text"]))
            : [Str(root["text"])];
        return string.Join(" ", parts.Select(part => part?.Trim()).Where(part => !string.IsNullOrEmpty(part)));
    }

    public static ApiException? Explain(HttpStatusCode status, string body, TimeSpan? retryAfter, bool speech = false)
    {
        var error = ParseObject(body)?["error"] as JsonObject;
        var message = Str(error?["message"]);
        var code = Str(error?["code"]);
        const string marker = "try again in ";
        int at = message?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        var wait = retryAfter ?? (at < 0 ? null : Duration(message![(at + marker.Length)..].Split(' ', 2)[0].TrimEnd('.', ',')));
        var restore = wait is { } time ? $" — он восстановится через {Tokens.Duration(time)}" : "";
        int number = (int)status;
        if (speech && number is 413 or 429)
            return new(number == 413 ? "Запись слишком большая для Groq. Продиктуйте сообщение по частям."
                : "Groq: исчерпан лимит распознавания речи вашего тарифа" + restore + ". Повторите позже.", status, code: code, retryAfter: wait);
        var text = (number, code) switch
        {
            (401, _) => "Groq не принял API-ключ. Проверьте его в настройках или создайте новый на console.groq.com/keys.",
            (403, _) => "Groq отказал в доступе (HTTP 403). Возможно, сервис недоступен в вашем регионе или ключ отключён в консоли Groq.",
            (413, _) => "Запрос больше, чем ваш тариф Groq пропускает за минуту" + (message != null && RequestSize().Match(message) is { Success: true } match
                ? $" (лимит {match.Groups[1].Value}, в запросе {match.Groups[2].Value} токенов)" : "") +
                ". Сократите сообщение и вложения, начните новый чат или выберите другого провайдера.",
            (429, _) => "Groq: исчерпан лимит запросов или токенов вашего тарифа" + restore +
                ". Нажмите «Повторить» позже или выберите другого провайдера в меню модели.",
            (400, "blocked_api_access") => "Groq остановил запросы: достигнут лимит расходов проекта. Измените его в консоли Groq.",
            (400, "tool_use_failed") => "Модель Groq неправильно вызвала инструмент. Нажмите «Повторить» или выберите другую модель.",
            _ => null,
        };
        return text == null ? null
            : new(text + ApiException.Tail(number, code, null), status, number == 429 ? wait <= ApiException.MaxWait : code == "tool_use_failed", code, wait);
    }

    static TimeSpan? Duration(string? text)
    {
        double seconds = 0;
        bool found = false;
        foreach (Match part in DurationPart().Matches(text ?? ""))
        {
            if (!double.TryParse(part.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            seconds += part.Groups[2].Value.ToLowerInvariant() switch { "h" => value * 3600, "m" => value * 60, "ms" => value / 1000, _ => value };
            found = true;
        }
        return found && seconds < 1e9 ? TimeSpan.FromSeconds(seconds) : null;
    }
}
