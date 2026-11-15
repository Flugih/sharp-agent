using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpAgent.Core;
using SharpAgent.Tools;

namespace SharpAgent.Agent;

internal sealed record IndexedSource(long Size, long Ticks, string Hash, bool Roslyn, SourceSyntax Syntax);

internal static partial class ProjectSourceIndex
{
    private const int MaxFiles = 6_000, MaxFileBytes = 384_000, MaxChangedBytes = 32_000_000;
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".py", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".java", ".kt", ".go", ".rs",
        ".c", ".cpp", ".h", ".hpp", ".swift", ".rb", ".php", ".ps1", ".sql", ".vue", ".svelte", ".xaml",
    };
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "build", "target", "vendor", "packages", "__pycache__",
        "coverage", "auth", "history", "temp", "attachments", "publish",
    };

    public static bool IsSource(string path) => SourceExtensions.Contains(Path.GetExtension(path));

    public static async Task<Dictionary<string, IndexedSource>> UpdateAsync(ToolContext context, string root, string folder,
        IEnumerable<string> files, bool canUseSdk, CancellationToken ct)
    {
        var index = new Dictionary<string, IndexedSource>(StringComparer.OrdinalIgnoreCase);
        var version = canUseSdk ? LocalDeveloperTools.ParserVersion : "";
        var cacheFile = Path.Combine(folder, "source-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("5|" + root.ToUpperInvariant() + "|" + version)))[..24] + ".json");
        var previous = Load(cacheFile);
        var csharp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int changedBytes = 0;
        bool changed = false;
        foreach (var name in files.Distinct(StringComparer.OrdinalIgnoreCase).Where(IsSource).Take(MaxFiles))
        {
            ct.ThrowIfCancellationRequested();
            if (!SafeSource(context, root, name, out var file)) continue;
            try
            {
                long ticks = file.LastWriteTimeUtc.Ticks, size = file.Length;
                var old = previous.GetValueOrDefault(name);
                bool preferRoslyn = version.Length > 0 && name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
                if (old != null && old.Size == size && old.Ticks == ticks && (!preferRoslyn || old.Roslyn))
                {
                    index[name] = old;
                    continue;
                }
                if (changedBytes + size > MaxChangedBytes) continue;
                var bytes = File.ReadAllBytes(file.FullName);
                if (bytes.Length > MaxFileBytes) continue;
                changedBytes += bytes.Length;
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (old != null && old.Hash == hash && (!preferRoslyn || old.Roslyn))
                {
                    index[name] = old with { Size = size, Ticks = ticks };
                }
                else
                {
                    using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
                    var text = reader.ReadToEnd();
                    if (text.Contains('\0')) continue;
                    if (preferRoslyn) csharp[name] = text;
                    index[name] = new(size, ticks, hash, false, preferRoslyn ? new([], []) : LocalSyntax(text));
                }
                changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var parsed = await LocalDeveloperTools.ParseCSharpAsync(folder, csharp, ct);
        foreach (var (name, source) in csharp)
            index[name] = parsed.TryGetValue(name, out var syntax)
                ? index[name] with { Syntax = syntax, Roslyn = true }
                : index[name] with { Syntax = LocalSyntax(source) };
        if (changed || index.Count != previous.Count) Save(cacheFile, index);
        return index;
    }

    public static bool SafeSource(ToolContext context, string root, string relative, out FileInfo file, bool allowMissing = false)
    {
        file = null!;
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (Path.IsPathRooted(relative) || parts.Any(part => part == ".." || part.StartsWith('.') || SkippedDirectories.Contains(part)) ||
            SensitiveName().IsMatch(Path.GetFileName(relative))) return false;
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
        catch (Exception ex) when (ex is IOException or ToolException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static IEnumerable<string> Walk(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        int visited = 0;
        while (pending.TryPop(out var folder) && visited++ < 1800)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in DirectoryEntries(folder))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.Name.StartsWith('.')) continue;
                if (entry is DirectoryInfo directory && !SkippedDirectories.Contains(entry.Name))
                    pending.Push(directory.FullName);
                else if (entry is FileInfo && IsSource(entry.Name))
                    yield return Path.GetRelativePath(root, entry.FullName);
            }
        }
    }

    private static FileSystemInfo[] DirectoryEntries(string folder)
    {
        try { return new DirectoryInfo(folder).EnumerateFileSystemInfos().Take(12_000).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static SourceSyntax LocalSyntax(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var declarations = new List<(string Name, int Line, string Signature, bool Body)>();
        for (int i = 0; i < lines.Length && declarations.Count < 600; i++)
        {
            var line = lines[i].Trim();
            if (line.Length > 600) continue;
            var match = Declaration().Match(line);
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                if (name is "if" or "for" or "while" or "switch" or "catch" or "return" or "new") continue;
                declarations.Add((name, i, line[..Math.Min(280, line.Length)], !match.Groups["type"].Success));
            }
        }
        return new(declarations.Select((declaration, i) =>
        {
            int end = declaration.Body ? Math.Min(declaration.Line + 39,
                i + 1 < declarations.Count ? declarations[i + 1].Line - 1 : lines.Length - 1) : declaration.Line;
            var body = string.Join("\n", lines[declaration.Line..(end + 1)]);
            var references = Identifier().Matches(body).Select(match => match.Value).Distinct().Take(40).ToArray();
            return new SourceSymbol(declaration.Name, declaration.Line + 1, end + 1, declaration.Signature, references);
        }).ToArray(), []);
    }

    private static Dictionary<string, IndexedSource> Load(string cache)
    {
        try
        {
            if (File.Exists(cache) && new FileInfo(cache).Length < 96_000_000 &&
                JsonSerializer.Deserialize<Dictionary<string, IndexedSource>>(File.ReadAllText(cache)) is { } index)
                return new(index, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static void Save(string path, Dictionary<string, IndexedSource> index)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temp)) JsonSerializer.Serialize(file, index);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [GeneratedRegex(@"(?:^|[._-])(?:secret|secrets|credentials|passwd|password|private[-_]?key|api[-_]?key|tokens)(?:[._-]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    [GeneratedRegex(@"^(?:(?:export|default|public|protected|private|internal|static|async|pub|unsafe|abstract|sealed|override|virtual)\s+)*(?:(?<type>class|struct|interface|enum|trait|record|module|namespace)\s+(?<name>[\p{L}_]\w*)|(?:def|function|func|fn|sub|fun)\s+(?:\([^)]*\)\s*)?(?<name>[\p{L}_]\w*)|(?:[\w.<>,\[\]?]+\s+)(?<name>[\p{L}_]\w*)\s*\(|(?<name>[\p{L}_]\w*)\s*\([^;]*\)\s*\{|(?:const|let|var)\s+(?<name>[\p{L}_]\w*)\s*=\s*.*=>)", RegexOptions.IgnoreCase)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_]{2,}")]
    internal static partial Regex Identifier();
}
