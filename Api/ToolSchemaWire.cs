using System.Buffers;
using System.Runtime.CompilerServices;

namespace SharpAgent.Api;

sealed class ToolSchemaWire(Func<JsonArray, JsonNode> project)
{
    readonly ConditionalWeakTable<JsonArray, byte[]> _cache = new();

    internal byte[]? Get(JsonArray? definitions) => definitions is { Count: > 0 }
        ? _cache.GetValue(definitions, items => JsonSerializer.SerializeToUtf8Bytes(project(items))) : null;

    internal static string Serialize(JsonObject body, byte[]? tools, byte[]? firstInput = null)
    {
        if (tools == null && firstInput == null) return body.ToJsonString();
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var (name, value) in body)
        {
            writer.WritePropertyName(name);
            if (name == "tools" && tools != null) writer.WriteRawValue(tools, skipInputValidation: true);
            else if (name == "input" && firstInput != null && value is JsonArray messages)
            {
                writer.WriteStartArray();
                writer.WriteRawValue(firstInput, skipInputValidation: true);
                foreach (var message in messages)
                    if (message != null) message.WriteTo(writer);
                    else writer.WriteNullValue();
                writer.WriteEndArray();
            }
            else if (value != null) value.WriteTo(writer);
            else writer.WriteNullValue();
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
