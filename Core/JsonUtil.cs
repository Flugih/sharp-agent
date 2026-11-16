using System.Text.Encodings.Web;

namespace SharpAgent.Core;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString();

    public static long Long(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => (long)d,
        _ => 0,
    };

    public static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static double? Double(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    public static JsonObject? ParseObject(string text)
    {
        static bool Loaded(JsonNode? node) => node is JsonObject map ? map.All(pair => Loaded(pair.Value)) : node is not JsonArray list || list.All(Loaded);
        try { return JsonNode.Parse(text) is JsonObject map && Loaded(map) ? map : null; }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return null; }
    }
}
