using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpAgent.Core;

namespace SharpAgent.Api;

public sealed record GroqLimits(long? Requests, long? RequestsLeft, DateTime? RequestsReset,
    long? Tokens, long? TokensLeft, DateTime? TokensReset, DateTime Updated);

public static partial class Groq
{
    public const string BaseUrl = "https://api.groq.com/openai/v1";
    public const string KeysUrl = "https://console.groq.com/keys";
    private const string SpeechModel = "whisper-large-v3-turbo";

    private static readonly string[] NonChat = ["whisper", "orpheus", "tts", "guard"];
    private static readonly string[] Fields = ["role", "content", "tool_calls", "tool_call_id"];
    private static readonly HttpClient Speech = new() { Timeout = TimeSpan.FromMinutes(5) };

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(ms|h|m|s)?", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPart();

    [GeneratedRegex(@"Limit (\d+).*?Requested (\d+)", RegexOptions.Singleline)]
    private static partial Regex RequestSize();

    public static bool IsChatModel(string id, JsonObject? model) =>
        JsonUtil.Bool(model?["active"]) != false &&
        !NonChat.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase));

    public static string[] EffortsOf(string id) =>
        id.StartsWith("openai/gpt-oss-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("qwen/qwen3", StringComparison.OrdinalIgnoreCase)
            ? ["low", "medium", "high"]
            : id.Contains("muse-spark", StringComparison.OrdinalIgnoreCase)
                ? ["low", "medium", "high", "xhigh"]
                : [];

    public static IEnumerable<JsonNode> Clean(IReadOnlyList<JsonNode> messages) => messages.Select(node =>
    {
        if (node is not JsonObject message) return node.DeepClone();
        var clean = new JsonObject();
        foreach (var field in Fields)
            if (message.TryGetPropertyValue(field, out var value)) clean[field] = value?.DeepClone();
        if (clean["tool_calls"] is JsonArray calls)
            clean["tool_calls"] = new JsonArray(calls.OfType<JsonObject>().Select(call => (JsonNode)new JsonObject
            {
                ["id"] = JsonUtil.Str(call["id"]),
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = JsonUtil.Str((call["function"] as JsonObject)?["name"]),
                    ["arguments"] = JsonUtil.Str((call["function"] as JsonObject)?["arguments"]) ?? "{}",
                },
            }).ToArray());
        return (JsonNode)clean;
    });

    public static GroqLimits? LimitsFrom(HttpResponseHeaders headers)
    {
        string? Get(string name) => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        long? Number(string name) => long.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        var now = DateTime.Now;
        DateTime? Reset(string name) => Duration(Get(name)) is { } wait ? now + wait : null;

        var limits = new GroqLimits(
            Number("x-ratelimit-limit-requests"), Number("x-ratelimit-remaining-requests"), Reset("x-ratelimit-reset-requests"),
            Number("x-ratelimit-limit-tokens"), Number("x-ratelimit-remaining-tokens"), Reset("x-ratelimit-reset-tokens"), now);
        return limits.Requests == null && limits.Tokens == null ? null : limits;
    }

    public static async Task<string> TranscribeAsync(string apiKey, byte[] wav, CancellationToken ct)
    {
        using var request = SpeechRequest(apiKey, wav);
        using var response = await Speech.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw SpeechError(response.StatusCode, body, response.Headers.RetryAfter?.Delta);
        return SpokenText(body);
    }

    private static HttpRequestMessage SpeechRequest(string apiKey, byte[] wav)
    {
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/audio/transcriptions")
        {
            Content = new MultipartFormDataContent
            {
                { new StringContent(SpeechModel), "model" },
                { new StringContent("verbose_json"), "response_format" },
                { audio, "file", "voice.wav" },
            },
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("SharpAgent/1.0");
        return request;
    }

    private static string SpokenText(string body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { throw new ApiException("Groq вернул не JSON: " + ApiException.Truncate(body, 200)); }
        IEnumerable<string?> parts = root?["segments"] is JsonArray { Count: > 0 } segments
            ? segments.OfType<JsonObject>().Where(Spoken).Select(segment => JsonUtil.Str(segment["text"]))
            : [JsonUtil.Str(root?["text"])];
        return string.Join(" ", parts.Select(part => part?.Trim()).Where(part => !string.IsNullOrEmpty(part)));
    }

    private static bool Spoken(JsonObject segment) =>
        !(JsonUtil.Double(segment["no_speech_prob"]) > 0.6 && JsonUtil.Double(segment["avg_logprob"]) < -1);

    private static ApiException SpeechError(HttpStatusCode status, string body, TimeSpan? retryAfter)
    {
        var (message, code) = ErrorOf(body);
        var wait = retryAfter ?? Duration(After(message, "try again in "));
        var text = (int)status switch
        {
            413 => "Запись слишком большая для Groq. Продиктуйте сообщение по частям.",
            429 => "Groq: исчерпан лимит распознавания речи вашего тарифа" +
                   (wait is { } time ? $" — он восстановится через {Tokens.Duration(time)}" : "") + ". Повторите позже.",
            _ => null,
        };
        return text != null
            ? new ApiException(text, status, code: code, retryAfter: wait)
            : Explain(status, body, retryAfter) ?? ApiException.FromResponse(status, body, null, retryAfter);
    }

    private static (string? Message, string? Code) ErrorOf(string body)
    {
        try
        {
            if (JsonNode.Parse(body)?["error"] is JsonObject error) return (JsonUtil.Str(error["message"]), JsonUtil.Str(error["code"]));
        }
        catch (JsonException) { }
        return (null, null);
    }

    public static ApiException? Explain(HttpStatusCode status, string body, TimeSpan? retryAfter)
    {
        var (message, code) = ErrorOf(body);
        var wait = retryAfter ?? Duration(After(message, "try again in "));
        int number = (int)status;
        var text = (number, code) switch
        {
            (401, _) => "Groq не принял API-ключ. Проверьте его в настройках или создайте новый на console.groq.com/keys.",
            (403, _) => "Groq отказал в доступе (HTTP 403). Возможно, сервис недоступен в вашем регионе или ключ отключён в консоли Groq.",
            (413, _) => "Запрос больше, чем ваш тариф Groq пропускает за минуту" + Size(message) +
                        ". Сократите сообщение и вложения, начните новый чат или выберите другого провайдера.",
            (429, _) => "Groq: исчерпан лимит запросов или токенов вашего тарифа" +
                        (wait is { } time ? $" — он восстановится через {Tokens.Duration(time)}" : "") +
                        ". Нажмите «Повторить» позже или выберите другого провайдера в меню модели.",
            (400, "blocked_api_access") => "Groq остановил запросы: достигнут лимит расходов проекта. Измените его в консоли Groq.",
            (400, "tool_use_failed") => "Модель Groq неправильно вызвала инструмент. Нажмите «Повторить» или выберите другую модель.",
            _ => null,
        };
        if (text == null) return null;
        bool retry = number == 429 ? wait <= ApiException.MaxWait : code == "tool_use_failed";
        return new ApiException(text + ApiException.Tail(number, code, null), status, retry, code, wait);
    }

    private static string? After(string? text, string marker)
    {
        int at = text?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        return at < 0 ? null : text![(at + marker.Length)..].Split(' ', 2)[0].TrimEnd('.', ',');
    }

    private static string Size(string? message) =>
        message != null && RequestSize().Match(message) is { Success: true } match
            ? $" (лимит {match.Groups[1].Value}, в запросе {match.Groups[2].Value} токенов)"
            : "";

    private static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        double seconds = 0;
        bool found = false;
        foreach (Match part in DurationPart().Matches(text))
        {
            if (!double.TryParse(part.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
            seconds += part.Groups[2].Value.ToLowerInvariant() switch { "h" => value * 3600, "m" => value * 60, "ms" => value / 1000, _ => value };
            found = true;
        }
        return found ? TimeSpan.FromSeconds(seconds) : null;
    }
}
