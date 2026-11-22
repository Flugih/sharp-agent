using static SharpAgent.Agent.AgentRunner;

namespace SharpAgent.Agent;

public static class HistoryCompressor
{
    const int CharsPerToken = 3, MessageTokens = 12, AttachmentTokens = 40;
    static readonly Regex ImportantText = new(@"\b(?:требован\w*|огранич\w*|услови\w*|сохрани\w*|решени\w*|важно|обязательно|только|не меня\w*|долж\w*|нельзя|без|must|required|only|preserve|decision|do not|never)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal sealed record ToolGroup(int Start, int End, bool Pending);

    public static int Estimate(JsonNode? node)
    {
        if (node is not JsonObject message) return (node?.ToJsonString(Compact).Length ?? 0) / CharsPerToken;
        int chars = (ExtractText(message["content"])?.Length ?? 0) + (Str(MetaOf(message)?["commentary"])?.Length ?? 0) + (ReviewOf(message)?.Length ?? 0) +
                    message.Where(p => p.Key is not ("role" or "content" or MetaField or AttachmentsField))
                        .Sum(p => p.Value?.ToJsonString(Compact).Length ?? 0);
        return chars / CharsPerToken + MessageTokens + Attachment.ListFrom(message).Count * AttachmentTokens;
    }

    public static int EstimateAll(IEnumerable<JsonNode> messages) => messages.Sum(Estimate);

    public static string Describe(string tool, string argumentsJson) => tool + Subject(tool, ParseArgs(argumentsJson));

    internal static List<ToolGroup> ToolGroups(IReadOnlyList<JsonNode> history)
    {
        var groups = new List<ToolGroup>();
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i]["tool_calls"] is not JsonArray { Count: > 0 } calls) continue;
            int start = i;
            var results = new HashSet<string>(StringComparer.Ordinal);
            while (i + 1 < history.Count && Role(history[i + 1]) == "tool")
                if (Str(history[++i]["tool_call_id"]) is { } id) results.Add(id);
            groups.Add(new(start, i, calls.OfType<JsonObject>().Any(call =>
                Str(call["id"]) is not { } id || !results.Contains(id))));
        }
        return groups;
    }

    internal static string Excerpt(string text, int limit)
    {
        if (text.Length <= limit) return text;
        const string gap = "\n…\n";
        int head = limit / 3, tail = limit / 4;
        var details = string.Join("\n", text[head..^tail].Split('\n').Select(line => line.Trim())
            .Where(line => ImportantText.IsMatch(line)).TakeLast(6).Select(line => Clip(line, 160)));
        return text[..head] + gap + (details.Length == 0 ? "" : Clip(details, limit - head - tail - 2 * gap.Length - 1) + gap) +
            text[^tail..];
    }

    static string Subject(string tool, JsonObject args)
    {
        string? Get(string key) => Str(args[key]) is { Length: > 0 } value ? value : null;
        var subject = tool switch
        {
            "exec_command" => Get("command") is { } command ? "`" + Clip(OneLine(command), 120) + "`" : null,
            "search_text" => $"«{Clip(OneLine(Get("query")), 60)}» в {Get("directory") ?? "рабочей папке"}" +
                             (Get("file_pattern") is { } mask ? $" ({mask})" : ""),
            "search_files" => $"{Get("pattern")} в {Get("directory") ?? "рабочей папке"}",
            "list_directory" => Get("path") ?? "рабочая папка",
            "move_path" or "copy_path" => $"{Get("source")} → {Get("destination")}",
            "run_plugin" => $"{Get("name")}/{Get("script")}",
            "use_plugin" => Get("name"),
            "generate_image" => Get("prompt") is { } prompt ? "«" + Clip(OneLine(prompt), 60) + "»" : null,
            "read_log" => Get("log_id"),
            ComputerTools.Name or ComputerTools.AppName => $"{Get("action")} {Get("element") ?? Get("path") ?? Get("text") ?? Get("window")}".TrimEnd(),
            "get_system_info" => null,
            _ => Get("path"),
        };
        return subject == null ? "" : " " + subject;
    }

    static JsonObject ParseArgs(string? json)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonObject { Count: > 0 } args ? args : new JsonObject();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return new JsonObject();
        }
    }

    static string OneLine(string? text) => (text ?? "").ReplaceLineEndings(" ").Trim();
}
