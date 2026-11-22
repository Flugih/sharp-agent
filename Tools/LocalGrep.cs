using System.IO.Enumeration;

namespace SharpAgent.Tools;

static class LocalGrep
{
    internal static (int Code, string Output)? Run(string command, string workDir, ToolContext context, CancellationToken ct)
    {
        var match = Regex.Match(command, @"^\s*rg(?:\.exe)?(?=\s|$)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var values = Regex.Matches(command[match.Length..], """(?:"([^"]*)"|'([^']*)'|([^\s"']+))""");
        var args = values.Select(token => token.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value).ToArray();
        if (values.Any(token => token.Groups[3].Success && token.Value.IndexOfAny(['|', ';', '<', '>', '&', '`', '$']) >= 0))
            throw new ToolException("Встроенный rg.exe недоступен. Резервный поиск принимает одну команду rg без конвейеров и перенаправления.");
        int contextLines = 0, limit = 250;
        bool files = false, literal = false, ignoreCase = false, flags = true;
        string? pattern = null;
        List<string> roots = [], globs = [];
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (flags && arg.StartsWith('-'))
            {
                switch (arg)
                {
                    case "--": flags = false; break;
                    case "--files": files = true; break;
                    case "-n" or "--line-number": break;
                    case "-i" or "--ignore-case": ignoreCase = true; break;
                    case "-F" or "--fixed-strings": literal = true; break;
                    case "-C" or "--context": contextLines = Number(0, 12); break;
                    case "-m" or "--max-count": limit = Number(1, 1000); break;
                    case "-g" or "--glob": globs.Add(Next()); break;
                    case "-e" or "--regexp": pattern = Next(); break;
                    default: throw new ToolException($"Встроенный rg.exe недоступен. Резервный поиск не поддерживает {arg}.");
                }
                continue;
            }
            if (pattern == null && !files) pattern = arg;
            else roots.Add(arg);
            continue;

            string Next() => ++i < args.Length ? args[i] : throw new ToolException($"Не указан аргумент {arg}.");
            int Number(int min, int max) => int.TryParse(Next(), out int number) ? Math.Clamp(number, min, max) :
                throw new ToolException($"Ожидается число после {arg}.");
        }
        if (!files && string.IsNullOrEmpty(pattern)) throw new ToolException("Не указано выражение для поиска.");
        var regex = files ? null : new Regex(literal ? Regex.Escape(pattern!) : pattern!,
            ignoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (roots.Count == 0) roots.Add(".");
        var output = new StringBuilder();
        int found = 0;
        foreach (var root in roots)
        {
            var path = context.Resolve(Path.GetFullPath(root, workDir));
            context.GuardSecrets(path);
            if (!Path.Exists(path)) throw new ToolException($"Путь поиска не найден: {path}");
            var paths = Directory.Exists(path) ? Walk(path, ct) : [path];
            foreach (var file in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (!PathHelper.SamePath(file, PathHelper.FinalPath(file)) || context.IsSecret(file) ||
                    !PathHelper.SamePath(file, context.Resolve(file))) continue;
                if (globs.Any(glob => glob.StartsWith('!') && Match(glob[1..], file)) ||
                    globs.Any(glob => !glob.StartsWith('!')) && !globs.Any(glob => !glob.StartsWith('!') && Match(glob, file))) continue;
                if (files)
                {
                    output.AppendLine(Path.GetRelativePath(workDir, file));
                    found++;
                }
                else try
                {
                    using var input = new StreamReader(file, detectEncodingFromByteOrderMarks: true);
                    Queue<(int Number, string Text)> before = [];
                    int number = 0, last = 0, until = 0;
                    while (input.ReadLine() is { } line)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (line.Contains('\0')) break;
                        number++;
                        bool hit = regex!.IsMatch(line);
                        if (hit)
                        {
                            foreach (var item in before)
                                if (item.Number > last) Append(item.Number, item.Text, '-');
                            found++;
                            until = number + contextLines;
                        }
                        if (number <= until) Append(number, line, hit ? ':' : '-');
                        before.Enqueue((number, line));
                        if (before.Count > contextLines) before.Dequeue();
                        if (found >= limit && number >= until || output.Length >= 2_000_000) break;
                    }

                    void Append(int at, string text, char separator)
                    {
                        output.Append(Path.GetRelativePath(workDir, file)).Append(separator).Append(at).Append(separator)
                            .AppendLine(AgentRunner.Clip(text, 12_000));
                        last = at;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (found >= limit || output.Length >= 2_000_000)
                    return (0, output.AppendLine("Достигнут лимит результатов.").ToString());
            }
        }
        return (found == 0 ? 1 : 0, output.ToString());
    }

    static bool Match(string glob, string path) => FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(path), true);

    static IEnumerable<string> Walk(string root, CancellationToken ct)
    {
        Stack<string> pending = new([root]);
        while (pending.TryPop(out var dir))
        {
            ct.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (entry.Name.StartsWith('.') || entry.Name is "node_modules" or "bin" or "obj" or "auth" or "data") continue;
                if (entry is DirectoryInfo folder) pending.Push(folder.FullName);
                else yield return entry.FullName;
            }
        }
    }
}
