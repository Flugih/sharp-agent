namespace SharpAgent.Tools;

static class PatchTool
{
    internal const string Name = "apply_patch";
    internal sealed record Change(string Path, byte[]? Before, byte[]? After);
    sealed record Hunk(string? Anchor, string[] Old, string[] New, bool Eof);
    sealed record Operation(string Kind, string Path, string? Move, string[] Content, Hunk[] Hunks);
    const int MaxFileBytes = 4 * 1024 * 1024;

    internal static AgentTool Tool() => new()
    {
        Name = Name,
        Description = "Apply a Windows workspace patch. Wrap it in *** Begin Patch / *** End Patch. Use *** Add File: (lines prefixed +), *** Delete File:, or *** Update File: (optionally *** Move to:). Within updates use @@ and space, - and + for context, removed and added lines.",
        Parameters = Schema.Object(new Prop("patch", "string", "The complete patch.", true)),
        Confirmation = (args, context) =>
        {
            var ops = Parse(args.Required("patch"));
            return new("Применить патч?", string.Join("\n", ops.Select(op => op.Kind + " " + Resolve(context, op.Path) +
                (op.Move == null ? "" : " → " + Resolve(context, op.Move)))), "Применить",
                Danger: ops.Any(op => op.Kind == "Delete" || op.Move != null || PathHelper.IsExecutable(op.Path)));
        },
        Handler = AgentTool.Sync((args, context, ct) =>
        {
            args.PatchChanges = Apply(context, args.Required("patch"), ct);
            return args.PatchChanges.Length == 0 ? "Изменений нет." :
                "Патч применён:\n" + string.Join("\n", args.PatchChanges.Select(change =>
                    (change.Before == null ? "+ " : change.After == null ? "- " : "~ ") + change.Path));
        }),
    };

    internal static string[] Paths(string patch) => [.. Parse(patch).SelectMany(op => op.Move == null ? [op.Path] : new[] { op.Path, op.Move })];

    static Operation[] Parse(string patch)
    {
        var lines = patch.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        if (lines.Length < 3 || lines[0] != "*** Begin Patch" || lines[^1] != "*** End Patch")
            throw new ToolException("Ожидается патч между *** Begin Patch и *** End Patch.");
        List<Operation> operations = [];
        for (int i = 1; i < lines.Length - 1;)
        {
            var header = lines[i++];
            int colon = header.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0 || !header.StartsWith("*** ", StringComparison.Ordinal) || !header[..colon].EndsWith(" File", StringComparison.Ordinal))
                throw new ToolException($"Недопустимый заголовок патча в строке {i}.");
            var kind = header[4..(colon - 5)];
            var path = header[(colon + 2)..];
            if (kind is not ("Add" or "Update" or "Delete") || string.IsNullOrWhiteSpace(path))
                throw new ToolException($"Недопустимая операция патча в строке {i}.");
            string? move = null;
            List<string> content = [];
            List<Hunk> hunks = [];
            if (kind == "Add")
            {
                while (lines[i].StartsWith('+')) content.Add(lines[i++][1..]);
                if (content.Count == 0) throw new ToolException("Добавляемые строки должны начинаться с +.");
            }
            else if (kind == "Update")
            {
                const string movePrefix = "*** Move to: ";
                if (lines[i].StartsWith(movePrefix, StringComparison.Ordinal)) move = lines[i++][movePrefix.Length..];
                while (lines[i] == "@@" || lines[i].StartsWith("@@ ", StringComparison.Ordinal))
                {
                    var anchor = lines[i].Length > 2 ? lines[i][3..] : null;
                    i++;
                    List<string> old = [], added = [];
                    int start = i;
                    while (lines[i].Length > 0 && lines[i][0] is ' ' or '+' or '-')
                    {
                        var line = lines[i++];
                        if (line[0] != '+') old.Add(line[1..]);
                        if (line[0] != '-') added.Add(line[1..]);
                    }
                    if (i == start) throw new ToolException("Пустой блок изменений.");
                    bool eof = lines[i] == "*** End of File";
                    if (eof) i++;
                    hunks.Add(new(anchor, [.. old], [.. added], eof));
                }
                if (hunks.Count == 0 && move == null) throw new ToolException("Нет изменений или пути назначения.");
            }
            operations.Add(new(kind, path, move, [.. content], [.. hunks]));
        }
        return [.. operations];
    }

    static Change[] Apply(ToolContext context, string patch, CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<Change>();
        foreach (var op in Parse(patch))
        {
            ct.ThrowIfCancellationRequested();
            var path = Target(op.Path);
            var before = Read(path);
            if ((before == null) != (op.Kind == "Add"))
                throw new ToolException(op.Kind == "Add" ? $"Файл уже существует: {path}" : $"Файл не найден: {path}");
            var after = op.Kind switch
            {
                "Add" => Encoding.UTF8.GetBytes(string.Join("\n", op.Content) + "\n"),
                "Update" => op.Hunks.Length == 0 ? before : Edit(before!, op.Hunks, path),
                _ => null,
            };
            if (after?.Length > MaxFileBytes) throw new ToolException($"Файл превышает размер патча: {path}");
            if (op.Move != null)
            {
                var destination = Target(op.Move);
                if (Read(destination) != null) throw new ToolException($"Файл уже существует: {destination}");
                changes.Add(new(path, before, null));
                changes.Add(new(destination, null, after));
            }
            else if (before == null || after == null || !before.AsSpan().SequenceEqual(after))
                changes.Add(new(path, before, after));
        }
        Commit(context, changes, ct);
        return [.. changes];

        string Target(string raw)
        {
            var path = Resolve(context, raw);
            if (!paths.Add(path)) throw new ToolException($"Путь встречается в патче дважды: {path}");
            if (Directory.Exists(path)) throw new ToolException($"Ожидается файл: {path}");
            context.FileAccess.Reserve(path);
            return path;
        }
    }

    static byte[] Edit(byte[] original, Hunk[] hunks, string path)
    {
        var (text, encoding) = TextFileIO.Decode(original);
        var endings = Regex.Matches(text, "\r\n|\r|\n").Select(match => match.Value).Distinct().Take(2).ToArray();
        if (endings.Length > 1) throw new ToolException($"Смешанные окончания строк: {path}");
        var ending = endings.FirstOrDefault() ?? "\n";
        bool finalNewline = text.EndsWith(ending, StringComparison.Ordinal);
        var lines = text.Length == 0 ? new List<string>() : text.Split(ending).ToList();
        if (finalNewline) lines.RemoveAt(lines.Count - 1);
        int cursor = 0;
        foreach (var hunk in hunks)
        {
            int start = cursor;
            if (hunk.Anchor != null)
            {
                int anchor = lines.FindIndex(cursor, line => line == hunk.Anchor);
                if (anchor < 0) throw new ToolException($"Строка @@ не найдена: {path}");
                start = anchor + 1;
            }
            int found = hunk.Old.Length == 0 ? (hunk.Eof || hunk.Anchor == null ? lines.Count : start) : -1;
            for (int at = start; hunk.Old.Length > 0 && at <= lines.Count - hunk.Old.Length; at++)
            {
                if (hunk.Eof && at + hunk.Old.Length != lines.Count) continue;
                if (!lines.Skip(at).Take(hunk.Old.Length).SequenceEqual(hunk.Old)) continue;
                if (found >= 0) throw new ToolException($"Контекст неоднозначен, добавь больше строк: {path}");
                found = at;
            }
            if (found < 0) throw new ToolException($"Контекст патча не совпадает: {path}");
            lines.RemoveRange(found, hunk.Old.Length);
            lines.InsertRange(found, hunk.New);
            cursor = found + hunk.New.Length;
        }
        var strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var updated = string.Join(ending, lines) + (lines.Count > 0 && (finalNewline || original.Length == 0) ? ending : "");
        return [.. encoding.Preamble, .. strict.GetBytes(updated)];
    }

    static void Commit(ToolContext context, List<Change> changes, CancellationToken ct)
    {
        var entries = changes.Select(change => new Staged(change, change.Path + ".sharp-agent-" + Guid.NewGuid().ToString("N"))).ToArray();
        var folders = new List<string>();
        bool complete = false;
        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                Verify(context, entry.Change);
                if (entry.Change.After is not { } after) continue;
                var missing = new Stack<string>();
                for (var dir = Path.GetDirectoryName(entry.Change.Path)!; !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)!)
                    missing.Push(dir);
                foreach (var dir in missing)
                {
                    Directory.CreateDirectory(dir);
                    folders.Add(dir);
                }
                using var output = new FileStream(entry.Staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(after);
            }
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                Verify(context, entry.Change);
                if (entry.Change.Before != null)
                {
                    if (File.GetAttributes(entry.Change.Path).HasFlag(FileAttributes.ReadOnly))
                        throw new ToolException($"Файл доступен только для чтения: {entry.Change.Path}");
                    File.Move(entry.Change.Path, entry.Backup);
                    entry.Displaced = true;
                }
                if (entry.Change.After != null)
                {
                    File.Move(entry.Staging, entry.Change.Path);
                    entry.Written = true;
                }
            }
            complete = true;
        }
        finally
        {
            List<string> recover = [];
            foreach (var entry in entries.Reverse())
            {
                try
                {
                    if (!complete && entry.Written) File.Delete(entry.Change.Path);
                    if (!complete && entry.Displaced) File.Move(entry.Backup, entry.Change.Path);
                    File.Delete(entry.Staging);
                    if (complete && entry.Displaced) File.Delete(entry.Backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { recover.Add(entry.Backup); }
            }
            if (!complete)
                foreach (var dir in folders.AsEnumerable().Reverse())
                    try { Directory.Delete(dir); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (!complete && recover.Count > 0) throw new ToolException("Не удалось полностью откатить патч. Оставлены резервные файлы:\n" + string.Join("\n", recover));
        }
    }

    sealed class Staged(Change change, string staging)
    {
        public Change Change { get; } = change;
        public string Staging { get; } = staging;
        public string Backup => Staging + ".bak";
        public bool Displaced { get; set; }
        public bool Written { get; set; }
    }

    static void Verify(ToolContext context, Change change)
    {
        Resolve(context, change.Path);
        var bytes = Read(change.Path);
        if (bytes == null ? change.Before != null : change.Before == null || !bytes.AsSpan().SequenceEqual(change.Before))
            throw new ToolException($"Файл изменился после проверки патча: {change.Path}");
    }

    static string Resolve(ToolContext context, string raw)
    {
        var path = context.Resolve(raw);
        context.GuardSecrets(path);
        context.GuardAppFiles(path);
        if (!PathHelper.SamePath(path, PathHelper.FinalPath(path)) || PathHelper.IsProtected(path))
            throw new ToolException($"Защищённый или перенаправленный путь: {path}");
        return path;
    }

    static byte[]? Read(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaxFileBytes) throw new ToolException($"Файл превышает размер патча: {path}");
        return File.ReadAllBytes(path);
    }
}
