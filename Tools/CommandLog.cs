namespace SharpAgent.Tools;

public static partial class CommandLog
{
    public const string ExitPrefix = "Код выхода: ";
    public const string TimeoutPrefix = "Прервано по таймауту";
    public const string GapPrefix = "… пропущено ";
    public const int KeepDays = 14;

    const string IdPrefix = "Журнал: ";
    const string NoOutput = "(нет вывода)";
    const int WholeLines = 60, WholeChars = 6_000, HeadLines = 15, TailLines = 25, ShownChars = 300;
    const int ImportantLines = 40, ImportantChars = 4_000, BriefChars = 160;
    const int DefaultPage = 200, MaxPage = 1_000, MaxPageChars = 50_000;

    public static IEnumerable<AgentTool> All() =>
    [
        new AgentTool
        {
            Name = "read_log",
            Description = "Дочитать вывод уже выполненной команды (run_command, run_plugin) по номеру журнала из строки " +
                "«Журнал: …» её результата — без повторного запуска. Журнал хранит первые и последние 256 КБ каждого " +
                "потока вывода. Возвращает пронумерованные строки и offset, с которого читать дальше; обрезанную строку " +
                $"целиком (до {MaxPageChars} символов) покажут её offset и limit=1.",
            Parameters = Schema.Object(
                new Prop("log_id", "string", "Номер журнала из результата команды.", Required: true),
                new Prop("offset", "integer", "С какой строки читать (нумерация с 1). По умолчанию 1."),
                new Prop("limit", "integer", $"Сколько строк прочитать (по умолчанию {DefaultPage}, максимум {MaxPage}).")),
            Handler = AgentTool.Sync(ReadLog),
        },
    ];

    public static string Summarize(string status, string output, string? folder)
    {
        var id = folder != null ? Save(folder, output.Length > 0 ? output : NoOutput) : null;
        var lines = output.Split('\n');
        bool whole = output.Length <= WholeChars && lines.Length <= WholeLines;
        var text = new StringBuilder(status).Append('\n');
        if (id != null) text.Append(IdPrefix).Append(id).Append(whole ? "" : $", строк {lines.Length} — подробности: read_log").Append('\n');
        if (output.Length == 0) return text.Append(NoOutput).ToString();
        if (whole) return text.Append(output).ToString();
        if (lines.Length <= HeadLines + TailLines) return AppendLines(text, lines, 0, lines.Length).ToString().TrimEnd();
        int tail = lines.Length - TailLines;
        var (important, dropped) = Important(lines, HeadLines, tail, ImportantChars);
        if (important.Count > 0)
        {
            text.Append("Важные строки:\n");
            foreach (var i in important) text.Append(i + 1).Append(": ").Append(Clip(lines[i].Trim(), ShownChars)).Append('\n');
            if (dropped > 0) text.Append($"… и ещё {dropped} — смотри read_log\n");
        }
        AppendLines(text.Append($"Начало (строки 1–{HeadLines}):\n"), lines, 0, HeadLines);
        return AppendLines(text.Append($"Конец (строки {tail + 1}–{lines.Length}):\n"), lines, tail, lines.Length).ToString().TrimEnd();
    }

    public static string Brief(string output)
    {
        var lines = output.Split('\n');
        var result = ResultOf(output) ?? Clip(lines[0].Trim(), BriefChars);
        var id = IdOf(output);
        int body = id != null ? 2 : 1;
        string? verdict = null;
        for (int i = lines.Length - 1; i >= body && verdict == null; i--)
            if (Summary().IsMatch(lines[i])) verdict = lines[i].Trim();
        if (ToolRegistry.CommandFailed(output)) verdict ??= lines.Skip(body).FirstOrDefault(line => Problem().IsMatch(line))?.Trim();
        return result + (verdict != null ? "; " + Clip(verdict, BriefChars) : "") + (id != null ? $" (журнал {id})" : "");
    }

    public static string Details(string output, int maxChars)
    {
        var lines = output.Split('\n');
        return string.Join("\n", Important(lines, IdOf(output) != null ? 2 : 1, lines.Length, maxChars).Picked
            .Select(i => Clip(lines[i].Trim(), ShownChars)));
    }

    public static string? ResultOf(string output)
    {
        var first = output.Split('\n', 2)[0].Trim();
        return first.StartsWith(ExitPrefix, StringComparison.Ordinal) ? "код " + first[ExitPrefix.Length..]
            : first.StartsWith(TimeoutPrefix, StringComparison.Ordinal) ? "таймаут" : null;
    }

    public static string? IdOf(string output)
    {
        var lines = output.Split('\n', 3);
        if (lines.Length < 2 || !lines[1].StartsWith(IdPrefix, StringComparison.Ordinal)) return null;
        var id = lines[1][IdPrefix.Length..];
        int comma = id.IndexOf(',');
        return (comma >= 0 ? id[..comma] : id).Trim();
    }

    public static string? PathOf(string folder, string id) => LogId().IsMatch(id) ? Path.Combine(folder, id + ".log") : null;

    public static string? Read(string folder, string id)
    {
        try { return PathOf(folder, id) is { } path && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Prune(string folder)
    {
        try
        {
            var expired = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.log"))
                try { if (file.LastWriteTime < expired) file.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string? Save(string folder, string text)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var id = $"{DateTime.Now:yyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";
            File.WriteAllText(Path.Combine(folder, id + ".log"), text, new UTF8Encoding(false));
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static string ReadLog(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var id = a.Required("log_id").Trim();
        var text = (c.LogsFolder != null ? Read(c.LogsFolder, id) : null) ??
            throw new ToolException($"Журнал «{id}» не найден: журналы хранятся {KeepDays} дней. Если вывод ещё нужен, выполни команду заново.");
        var lines = text.Split('\n');
        int offset = Math.Max(1, a.Int("offset", 1));
        if (offset > lines.Length) throw new ToolException($"offset={offset} больше числа строк в журнале ({lines.Length}).");
        int last = Math.Min(lines.Length, offset + Math.Clamp(a.Int("limit", DefaultPage), 1, MaxPage) - 1), end = offset - 1;
        var body = new StringBuilder();
        for (int i = offset; i <= last; i++)
        {
            var line = $"{i}: {FileTools.ClipLine(lines[i - 1], i, single: last == offset)}\n";
            if (i > offset && body.Length + line.Length > MaxPageChars) break;
            body.Append(line);
            end = i;
        }
        return $"Журнал {id}: строки {offset}–{end} из {lines.Length}\n{body}" +
            (end < lines.Length ? $"Дальше: offset={end + 1}" : "Это конец журнала.");
    }

    static (List<int> Picked, int Dropped) Important(string[] lines, int from, int to, int maxChars)
    {
        var picked = new List<int>();
        var seen = new HashSet<string>();
        int dropped = 0, chars = 0, context = 0;
        for (int i = from; i < to; i++)
        {
            var line = lines[i].Trim();
            bool problem = Problem().IsMatch(line);
            bool wanted = problem || context > 0 || Summary().IsMatch(line) || Location().IsMatch(line);
            context = problem ? 2 : Math.Max(0, context - 1);
            if (!wanted || line.Length == 0 || !seen.Add(line)) continue;
            int size = Math.Min(line.Length, ShownChars) + 8;
            if (!line.StartsWith(GapPrefix, StringComparison.Ordinal) && (picked.Count >= ImportantLines || chars + size > maxChars))
            {
                dropped++;
                continue;
            }
            picked.Add(i);
            chars += size;
        }
        return (picked, dropped);
    }

    static StringBuilder AppendLines(StringBuilder text, string[] lines, int from, int to)
    {
        for (int i = from; i < to; i++) text.Append(Clip(lines[i], ShownChars)).Append('\n');
        return text;
    }

    static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    [GeneratedRegex("error|exception|fail|fatal|traceback|panic|warn|denied|not recognized|cannot|could not|unable to|●|✕|✗|" +
                    "ошибк|исключени|не удалось|не уда[её]тся|невозможно|не может|не распознан|не является внутренней|" +
                    "предупрежд|отказ|не найден", RegexOptions.IgnoreCase)]
    private static partial Regex Problem();

    [GeneratedRegex(@"passed|succeeded|success|total|tests? run|пройден|успешно|всего|итого|^" + GapPrefix + @"\d+ байт", RegexOptions.IgnoreCase)]
    private static partial Regex Summary();

    [GeneratedRegex(@"line \d+|строка \d+|\.\w{1,6}[:(]\d+", RegexOptions.IgnoreCase)]
    private static partial Regex Location();

    [GeneratedRegex("^[0-9a-z-]{1,40}$")]
    private static partial Regex LogId();
}
