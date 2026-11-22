namespace SharpAgent.Agent;

sealed record IndexedSource(long Size, long Ticks, string Hash, bool Roslyn, SourceSyntax Syntax, string? ProjectRevision = null);

static partial class ProjectSourceIndex
{
    const int MaxFiles = 6_000, MaxFileBytes = 384_000, MaxChangedBytes = 32_000_000;
    static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".py", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".java", ".kt", ".go", ".rs",
        ".c", ".cpp", ".h", ".hpp", ".swift", ".rb", ".php", ".ps1", ".sql", ".vue", ".svelte", ".xaml",
    };
    static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "build", "target", "vendor", "packages", "__pycache__",
        "coverage", "auth", "history", "temp", "attachments", "publish", "data", "data.migrating",
    };

    public static bool IsSource(string path) => SourceExtensions.Contains(Path.GetExtension(path));

    public static async Task<Dictionary<string, IndexedSource>> UpdateAsync(ToolContext context, string root, string folder,
        IEnumerable<string> files, bool canUseSdk, CancellationToken ct, Dictionary<string, IndexedSource>? baseline = null,
        HashSet<string>? rebind = null)
    {
        var index = baseline == null ? new Dictionary<string, IndexedSource>(StringComparer.OrdinalIgnoreCase) : new(baseline, StringComparer.OrdinalIgnoreCase);
        var version = canUseSdk ? LocalDeveloperTools.ParserVersion : "";
        var project = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("9|" + root.ToUpperInvariant() + "|" + version)));
        var projects = new SourceProjects(context, root);
        using var database = OpenDatabase(context);
        var cacheFile = Path.Combine(folder, "source-" + project[..24] + ".json");
        Dictionary<string, IndexedSource> previous;
        try { previous = baseline ?? database?.Read(project) ?? Load(cacheFile); }
        catch (Exception ex) when (ex is IOException or JsonException) { previous = Load(cacheFile); }
        var csharp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int changedBytes = 0;
        bool changed = false;
        foreach (var name in files.Distinct(StringComparer.OrdinalIgnoreCase).Where(IsSource).Take(MaxFiles))
        {
            ct.ThrowIfCancellationRequested();
            if (!SafeSource(context, root, name, out var file))
            {
                changed |= index.Remove(name);
                continue;
            }
            try
            {
                long ticks = file.LastWriteTimeUtc.Ticks, size = file.Length;
                bool preferRoslyn = version.Length > 0 && name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
                var revision = preferRoslyn ? projects.Of(name).Revision : null;
                var old = previous.GetValueOrDefault(name) is { } cached && (!preferRoslyn || cached.Roslyn && cached.ProjectRevision == revision) ? cached : null;
                bool force = rebind?.Contains(name) == true;
                if (old != null && old.Size == size && old.Ticks == ticks && !force)
                {
                    index[name] = old;
                    continue;
                }
                if (changedBytes + size > MaxChangedBytes) continue;
                var bytes = File.ReadAllBytes(file.FullName);
                if (bytes.Length > MaxFileBytes) continue;
                changedBytes += bytes.Length;
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (old != null && old.Hash == hash && !force) index[name] = old with { Size = size, Ticks = ticks };
                else
                {
                    var text = new StreamReader(new MemoryStream(bytes), true).ReadToEnd();
                    if (text.Contains('\0')) continue;
                    if (preferRoslyn) csharp[name] = text;
                    index[name] = new(size, ticks, hash, false, preferRoslyn ? new([], []) : LocalSyntax(text), revision);
                }
                changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var support = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (csharp.Count > 0)
        {
            var names = csharp.Values.SelectMany(text => Identifier().Matches(text).Select(match => match.Value))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var entry in previous.Where(entry => !csharp.ContainsKey(entry.Key) && entry.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                         entry.Value.Syntax.Symbols.Any(symbol => names.Contains(symbol.Name))).Take(40))
            {
                ct.ThrowIfCancellationRequested();
                if (SafeSource(context, root, entry.Key, out var file) && TextFileIO.TryRead(file.FullName, MaxFileBytes) is { } text)
                    support[entry.Key] = text;
            }
        }
        var parsed = await LocalDeveloperTools.ParseCSharpAsync(folder, csharp, support, projects, ct);
        foreach (var (name, source) in csharp)
            index[name] = parsed.TryGetValue(name, out var syntax)
                ? index[name] with { Syntax = syntax, Roslyn = true }
                : index[name] with { Syntax = LocalSyntax(source) };
        if (changed || index.Count != previous.Count)
        {
            try
            {
                if (database != null) database.Update(project, previous, index);
                else Save(cacheFile, index);
            }
            catch (IOException) { Save(cacheFile, index); }
        }
        return index;
    }

    internal static bool Trackable(string relative) => relative != "." && !Path.IsPathRooted(relative) &&
        !relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(p => p.StartsWith('.') || SkippedDirectories.Contains(p));

    static ProjectIndexStore? OpenDatabase(ToolContext context)
    {
        try { return new(context.DatabaseFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public static bool SafeSource(ToolContext context, string root, string relative, out FileInfo file, bool allowMissing = false)
    {
        file = null!;
        if (!Trackable(relative) || SensitiveName().IsMatch(Path.GetFileName(relative)))
            return false;
        try
        {
            var path = context.Resolve(Path.Combine(root, relative));
            var canonical = PathHelper.FinalPath(path);
            if (!PathHelper.SamePath(path, canonical) || !PathHelper.IsInside(canonical, root)) return false;
            context.GuardSecrets(canonical);
            file = new FileInfo(canonical);
            return file.Exists
                ? file.Length <= MaxFileBytes && (file.Attributes & FileAttributes.ReparsePoint) == 0
                : allowMissing && !Directory.Exists(canonical);
        }
        catch (Exception ex) when (ex is IOException or ToolException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    public static IEnumerable<string> Walk(string root, CancellationToken ct)
    {
        Stack<string> pending = new([root]);
        int visited = 0;
        while (pending.TryPop(out var folder) && visited++ < 1800)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in DirectoryEntries(folder))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.Name.StartsWith('.')) continue;
                if (entry is DirectoryInfo directory && !SkippedDirectories.Contains(entry.Name)) pending.Push(directory.FullName);
                else if (entry is FileInfo && IsSource(entry.Name)) yield return Path.GetRelativePath(root, entry.FullName);
            }
        }
    }

    static FileSystemInfo[] DirectoryEntries(string folder)
    {
        try { return new DirectoryInfo(folder).EnumerateFileSystemInfos().Take(12_000).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    static SourceSyntax LocalSyntax(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var declarations = new List<(string Name, int Line, string Signature, bool Body)>();
        for (int i = 0; i < lines.Length && declarations.Count < 600; i++)
        {
            var line = lines[i].Trim();
            if (line.Length <= 600 && Declaration().Match(line) is { Success: true } match &&
                match.Groups["name"].Value is not ("if" or "for" or "while" or "switch" or "catch" or "return" or "new") and var name)
                declarations.Add((name, i, line[..Math.Min(280, line.Length)], !match.Groups["type"].Success));
        }
        return new(declarations.Select((declaration, i) =>
        {
            int end = declaration.Body ? Math.Min(declaration.Line + 39,
                i + 1 < declarations.Count ? declarations[i + 1].Line - 1 : lines.Length - 1) : declaration.Line;
            var references = Identifier().Matches(string.Join("\n", lines[declaration.Line..(end + 1)]))
                .Select(m => m.Value).Distinct().Take(40).ToArray();
            return new SourceSymbol(declaration.Name, declaration.Line + 1, end + 1, declaration.Signature, references);
        }).ToArray(), []);
    }

    static Dictionary<string, IndexedSource> Load(string cache)
    {
        try
        {
            if (File.Exists(cache) && new FileInfo(cache).Length < 96_000_000 &&
                JsonSerializer.Deserialize<Dictionary<string, IndexedSource>>(File.ReadAllText(cache)) is { } index)
                return new(index, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    static void Save(string path, Dictionary<string, IndexedSource> index)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temp)) JsonSerializer.Serialize(file, index);
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [GeneratedRegex(@"(?:^|[._-])(?:secret|secrets|credentials|passwd|password|private[-_]?key|api[-_]?key|tokens)(?:[._-]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    [GeneratedRegex(@"^(?:(?:export|default|public|protected|private|internal|static|async|pub|unsafe|abstract|sealed|override|virtual)\s+)*(?:(?<type>class|struct|interface|enum|trait|record|module|namespace)\s+(?<name>[\p{L}_]\w*)|(?:def|function|func|fn|sub|fun)\s+(?:\([^)]*\)\s*)?(?<name>[\p{L}_]\w*)|(?:[\w.<>,\[\]?]+\s+)(?<name>[\p{L}_]\w*)\s*\(|(?<name>[\p{L}_]\w*)\s*\([^;]*\)\s*\{|(?:const|let|var)\s+(?<name>[\p{L}_]\w*)\s*=\s*.*=>)", RegexOptions.IgnoreCase)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_]{2,}")]
    internal static partial Regex Identifier();
}
