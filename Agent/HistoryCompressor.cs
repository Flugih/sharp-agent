using static SharpAgent.Agent.AgentRunner;

namespace SharpAgent.Agent;

public static class HistoryCompressor
{
    const int CharsPerToken = 3, MessageTokens = 12, AttachmentTokens = 40;
    const int FailureChars = 1_500, ErrorChars = 600, ToolHeadChars = 3_000, ToolTailChars = 1_000;
    const string Header =
        "[Сводка программы: давняя переписка свёрнута, чтобы сэкономить память модели. Это журнал прошлых запросов и действий, " +
        "а не новая просьба; если нужны подробности, получи их заново инструментами.]";
    const string NextHeader = "[Сводка программы, продолжение]";
    static readonly Regex ImportantText = new(@"\b(?:требован\w*|огранич\w*|услови\w*|сохрани\w*|решени\w*|важно|обязательно|только|не меня\w*|долж\w*|нельзя|без|must|required|only|preserve|decision|do not|never)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    sealed record Unit(int Start, int End, bool User);
    sealed record Call(string Name, JsonObject Args, string Output);

    public static int Estimate(JsonNode? node)
    {
        if (node is not JsonObject message) return (node?.ToJsonString(Compact).Length ?? 0) / CharsPerToken;
        int chars = (ExtractText(message["content"])?.Length ?? 0) + (Str(MetaOf(message)?["commentary"])?.Length ?? 0) +
                    message.Where(p => p.Key is not ("role" or "content" or MetaField or AttachmentsField))
                        .Sum(p => p.Value?.ToJsonString(Compact).Length ?? 0);
        return chars / CharsPerToken + MessageTokens + Attachment.ListFrom(message).Count * AttachmentTokens;
    }

    public static int EstimateAll(IEnumerable<JsonNode> messages) => messages.Sum(Estimate);

    public static string Describe(string tool, string argumentsJson) => tool + Subject(tool, ParseArgs(argumentsJson));

    public static List<JsonNode> Compress(IReadOnlyList<JsonNode> history, int room, bool harder)
    {
        if (!harder && EstimateAll(history) <= room) return [.. history];
        var units = Split(history);
        int currentUser = units.FindLast(unit => unit.User)?.Start ?? -1;
        var steps = units.Where(unit => !unit.User).ToList();
        var lines = steps.ToDictionary(step => step, step => Lines(history, step, harder));
        int total = units.Where(unit => unit.User).Sum(unit => Size(history, unit)) + steps.Sum(step => LineTokens(lines[step]));
        int lastStepDelta = steps.Count == 0 ? 0 : Size(history, steps[^1], harder) - LineTokens(lines[steps[^1]]);
        var projected = history.ToArray();
        foreach (var unit in units)
        {
            if (total + lastStepDelta <= room) break;
            int index = unit.Start;
            if (unit.User && index != currentUser && projected[index] is JsonObject message &&
                ExtractText(message["content"]) is { } request && request.Length > (harder ? 600 : 1_200))
            {
                var copy = (JsonObject)message.DeepClone();
                copy["content"] = "[Сводка прежнего запроса]\n" + Excerpt(request, harder ? 600 : 1_200);
                total += Estimate(copy) - Estimate(message);
                projected[index] = copy;
            }
        }
        int keepFrom = steps.Count;
        for (int k = steps.Count - 1; k >= 0; k--)
        {
            int delta = k == steps.Count - 1 ? lastStepDelta : Size(history, steps[k]) - LineTokens(lines[steps[k]]);
            if (k < steps.Count - 1 && (harder || total + delta > room)) break;
            total += delta;
            keepFrom = k;
        }
        AddLatestFailure(history, steps, keepFrom, lines);
        var result = Assemble(projected, units, keepFrom, lines, harder);
        if (currentUser <= 0 || EstimateAll(result) <= room) return result;

        int start = result.FindLastIndex(message => Role(message) == "user");
        var latest = result.GetRange(start, result.Count - start);
        int available = (room - EstimateAll(latest) - MessageTokens) * CharsPerToken;
        if (available > 300)
        {
            var previous = units.TakeWhile(unit => unit.Start < currentUser)
                .SelectMany(unit => unit.User ? ["- Запрос: " + Excerpt(ExtractText(projected[unit.Start]["content"]) ?? "", 450)] : lines[unit])
                .ToList();
            latest.Insert(0, new JsonObject { ["role"] = "assistant", ["content"] = Clip(Summary(Header, previous, harder), available - 1) });
        }
        return latest;
    }

    static List<Unit> Split(IReadOnlyList<JsonNode> history)
    {
        var units = new List<Unit>();
        for (int i = 0; i < history.Count; i++)
        {
            bool user = Role(history[i]) == "user";
            int end = i;
            while (!user && end + 1 < history.Count && Role(history[end + 1]) == "tool") end++;
            units.Add(new(i, end, user));
            i = end;
        }
        return units;
    }

    static List<JsonNode> Assemble(IReadOnlyList<JsonNode> history, List<Unit> units, int keepFrom, Dictionary<Unit, List<string>> lines,
        bool harder)
    {
        var result = new List<JsonNode>();
        var pending = new List<string>();
        bool headed = false;
        string TakeSummary()
        {
            var summary = Summary(headed ? NextHeader : Header, pending, harder);
            headed = true;
            pending.Clear();
            return summary;
        }
        void Flush()
        {
            if (pending.Count > 0) result.Add(new JsonObject { ["role"] = "assistant", ["content"] = TakeSummary() });
        }
        int step = 0;
        foreach (var unit in units)
        {
            if (unit.User)
            {
                Flush();
                result.Add(history[unit.Start]);
                continue;
            }
            if (step++ < keepFrom)
            {
                pending.AddRange(lines[unit]);
                continue;
            }
            if (pending.Count > 0 && Role(history[unit.Start]) == "assistant")
            {
                var merged = (JsonObject)history[unit.Start].DeepClone();
                var text = ExtractText(merged["content"]);
                merged["content"] = TakeSummary() + (string.IsNullOrWhiteSpace(text) ? "" : "\n\n" + text);
                result.Add(merged);
            }
            else
            {
                Flush();
                result.Add(history[unit.Start]);
            }
            for (int i = unit.Start + 1; i <= unit.End; i++) result.Add(harder ? Shorten(history[i]) : history[i]);
        }
        Flush();
        return result;
    }

    internal static JsonNode Shorten(JsonNode tool)
    {
        if (Str(tool["content"]) is not { Length: > ToolHeadChars + ToolTailChars } content) return tool;
        int head = content.LastIndexOf('\n', ToolHeadChars) is > ToolHeadChars / 2 and var end ? end : ToolHeadChars;
        int tail = content.IndexOf('\n', content.Length - ToolTailChars, ToolTailChars - 1) is >= 0 and var start
            ? start + 1
            : content.Length - ToolTailChars;
        string skipped = content[head..tail], rest = content[tail..];
        bool tailMidLine = skipped[^1] != '\n';
        int missing = skipped.Count(c => c == '\n') - 1 + (skipped[0] == '\n' ? 0 : 1) + (tailMidLine ? 1 : 0);
        int after = rest.Count(c => c == '\n') + (tailMidLine ? 0 : 1);
        return new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = tool["tool_call_id"]?.DeepClone(),
            ["content"] = content[..head] + $"\n… (сокращено, чтобы поместиться в память модели: пропущено строк: {missing}, " +
                          $"строк после пропуска: {after}; заголовок описывает полный результат — пропущенное получи заново частями) …\n" +
                          rest,
        };
    }

    static string Summary(string header, List<string> lines, bool harder)
    {
        var rows = new List<(string Line, int Count)>();
        foreach (var line in lines)
        {
            if (rows.Count > 0 && rows[^1].Line == line) rows[^1] = (line, rows[^1].Count + 1);
            else rows.Add((line, 1));
        }
        var shown = rows.Select(row => row.Count > 1 ? $"{row.Line} (×{row.Count})" : row.Line).ToList();
        int cap = harder ? 20 : 60;
        if (shown.Count > cap) shown = [$"- … и ещё {shown.Count - cap + 1} более ранних действий", .. shown.TakeLast(cap - 1)];
        return header + "\n" + string.Join("\n", shown);
    }

    static List<string> Lines(IReadOnlyList<JsonNode> history, Unit step, bool harder)
    {
        var lines = Calls(history, step).Select(call => $"- {call.Name}{Subject(call.Name, call.Args)} → {Result(call)}").ToList();
        if (history[step.Start] is JsonObject message && Role(message) == "assistant" && message["tool_calls"] == null &&
            ExtractText(message["content"]) is { } text && !string.IsNullOrWhiteSpace(text))
            lines.Add("- Ответ: " + Excerpt(text, harder ? 200 : 600));
        return lines;
    }

    static string Excerpt(string text, int limit)
    {
        if (text.Length <= limit) return text;
        const string gap = "\n…\n";
        int head = limit / 3, tail = limit / 4;
        var details = string.Join("\n", text[head..^tail].Split('\n').Select(line => line.Trim())
            .Where(line => ImportantText.IsMatch(line)).TakeLast(6).Select(line => Clip(line, 160)));
        return text[..head] + gap + (details.Length == 0 ? "" : Clip(details, limit - head - tail - 2 * gap.Length - 1) + gap) +
            text[^tail..];
    }

    static void AddLatestFailure(IReadOnlyList<JsonNode> history, List<Unit> steps, int keepFrom, Dictionary<Unit, List<string>> lines)
    {
        var latest = new Dictionary<string, (int Step, Call Call)>();
        for (int k = 0; k < steps.Count; k++)
            foreach (var call in Calls(history, steps[k]))
                latest[call.Name + Subject(call.Name, call.Args)] = (k, call);
        (int Step, Call? Call) failure = (-1, null);
        foreach (var (step, call) in latest.Values)
            if (step > failure.Step && Failed(call)) failure = (step, call);
        if (failure.Call is not { } failed || failure.Step >= keepFrom) return;
        var details = failed.Name is "run_command" or "run_plugin" && !ToolRegistry.IsErrorOutput(failed.Output)
            ? CommandLog.Details(failed.Output, FailureChars)
            : Clip(string.Join("\n", failed.Output.Split('\n').Take(3)), ErrorChars);
        int at = failure.Step;
        while (at + 1 < keepFrom && steps[at + 1].Start == steps[at].End + 1) at++;
        if (details.Length > 0)
            lines[steps[at]].Add($"- Последняя ошибка ({failed.Name}{Subject(failed.Name, failed.Args)}) подробно:\n  " +
                                 details.Replace("\n", "\n  "));
    }

    static bool Failed(Call call) =>
        call.Output.StartsWith("Ошибка", StringComparison.Ordinal) ||
        call.Name is "run_command" or "run_plugin" && ToolRegistry.CommandFailed(call.Output);

    static IEnumerable<Call> Calls(IReadOnlyList<JsonNode> history, Unit step)
    {
        if (history[step.Start] is not JsonObject assistant || assistant["tool_calls"] is not JsonArray calls) yield break;
        var outputs = new Dictionary<string, string>();
        for (int i = step.Start + 1; i <= step.End; i++)
            if (history[i] is JsonObject tool)
                outputs[Str(tool["tool_call_id"]) ?? ""] = (Str(tool["content"]) ?? "").Replace(RepeatReply, "");
        foreach (var call in calls.OfType<JsonObject>())
        {
            var function = call["function"] as JsonObject;
            yield return new(Str(function?["name"]) ?? "?", ParseArgs(Str(function?["arguments"])),
                outputs.GetValueOrDefault(Str(call["id"]) ?? "", ""));
        }
    }

    static string Subject(string tool, JsonObject args)
    {
        string? Get(string key) => Str(args[key]) is { Length: > 0 } value ? value : null;
        var subject = tool switch
        {
            "run_command" => Get("command") is { } command ? "`" + Clip(OneLine(command), 120) + "`" : null,
            "search_text" => $"«{Clip(OneLine(Get("query")), 60)}» в {Get("directory") ?? "рабочей папке"}" +
                             (Get("file_pattern") is { } mask ? $" ({mask})" : ""),
            "search_files" => $"{Get("pattern")} в {Get("directory") ?? "рабочей папке"}",
            "list_directory" => Get("path") ?? "рабочая папка",
            "move_path" or "copy_path" => $"{Get("source")} → {Get("destination")}",
            "run_plugin" => $"{Get("name")}/{Get("script")}",
            "use_plugin" => Get("name"),
            "generate_image" => Get("prompt") is { } prompt ? "«" + Clip(OneLine(prompt), 60) + "»" : null,
            "read_log" => Get("log_id"),
            "get_system_info" => null,
            _ => Get("path"),
        };
        return subject == null ? "" : " " + subject;
    }

    static string Result(Call call)
    {
        var output = call.Output;
        var first = output.Split('\n', 2)[0].Trim();
        if (call.Name is "run_command" or "run_plugin") return CommandLog.Brief(output);
        if (ToolRegistry.IsErrorOutput(output)) return Clip(first, 200);
        return call.Name switch
        {
            "read_file" => "прочитан" + string.Concat(first.Split(" | ").Skip(3)
                .Where(part => part.StartsWith("строк", StringComparison.Ordinal) || part.StartsWith("показаны", StringComparison.Ordinal))
                .Select(part => ", " + part)),
            "search_text" => Clip(output.TrimEnd().Split('\n')[^1].Trim(), 200),
            "search_files" => first.StartsWith("Ничего", StringComparison.Ordinal) ? "ничего не найдено" : $"найдено: {Count(output) - 1}",
            "list_directory" => output.Contains("(папка пуста)") ? "папка пуста" : $"элементов: {Count(output) - 1}",
            "get_file_info" => first.StartsWith("Не существует", StringComparison.Ordinal) ? "не существует" : "сведения получены",
            "get_system_info" => "сведения получены",
            "use_plugin" => "инструкция получена",
            "generate_image" or "read_log" => Clip(first, 200),
            _ => first.IndexOf(": ", StringComparison.Ordinal) is > 0 and var colon ? first[..colon] : Clip(first, 160),
        };
    }

    static int Count(string output) => output.Split('\n').Count(line => line.Trim().Length > 0);

    static int Size(IReadOnlyList<JsonNode> history, Unit unit, bool shortenTools = false)
    {
        int size = Estimate(history[unit.Start]);
        for (int i = unit.Start + 1; i <= unit.End; i++) size += Estimate(shortenTools ? Shorten(history[i]) : history[i]);
        return size;
    }

    static int LineTokens(List<string> lines) => lines.Sum(line => line.Length / CharsPerToken + 1);

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
