namespace SharpAgent.Agent;

internal sealed class NextActionStream(Action<string?, bool> emit)
{
    internal const string Prefix = "\n<sharp-next>", End = "</sharp-next>";
    string _tail = "";
    bool _hidden;

    public void Push(string? text, bool usesTools)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (text == null)
            {
                _tail = "";
                _hidden = false;
            }
            emit(text, usesTools);
            return;
        }
        if (_hidden) return;
        var chunk = _tail + text;
        int at = chunk.IndexOf(Prefix, StringComparison.Ordinal);
        _hidden = at >= 0;
        int hold = _hidden ? 0 : Math.Min(chunk.Length, Prefix.Length - 1);
        while (hold > 0 && !chunk.AsSpan(^hold..).SequenceEqual(Prefix.AsSpan(0, hold))) hold--;
        _tail = hold > 0 ? chunk[^hold..] : "";
        int visible = _hidden ? at : chunk.Length - hold;
        if (visible > 0) emit(chunk[..visible], usesTools);
    }
}

partial class AgentRunner
{
    const string NextActionsField = "next_actions";

    internal static string[] NextActionsOf(JsonNode? message) => ReadNextActions(MetaOf(message)?[NextActionsField]);

    static string[] ReadNextActions(JsonNode? payload) => payload is JsonArray actions
        ? actions.Select(Str).OfType<string>().Select(action => action.Trim())
            .Where(action => action.Length is > 0 and <= 64 && !action.Any(char.IsControl))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray()
        : [];

    static string[] ExtractNextActions(JsonObject message, ref string? text)
    {
        if (text == null) return [];
        int at = text.IndexOf(NextActionStream.Prefix, StringComparison.Ordinal);
        if (at < 0) return [];
        var payload = text.AsSpan(at + NextActionStream.Prefix.Length).Trim();
        text = text[..at].TrimEnd();
        message["content"] = text;
        if (payload.Length > 512 || !payload.EndsWith(NextActionStream.End, StringComparison.Ordinal)) return [];
        try { return ReadNextActions(JsonNode.Parse(payload[..^NextActionStream.End.Length].ToString())); }
        catch (JsonException) { return []; }
    }
}
