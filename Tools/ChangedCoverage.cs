using System.Xml;
using System.Xml.Linq;

namespace SharpAgent.Tools;

static partial class ChangedCoverage
{
    internal sealed record Edit(string Path, FileDiff Diff);

    internal static string? Latest(ToolContext context, string root, DateTime startUtc, IReadOnlyList<Edit> edits)
    {
        if (edits.Count == 0) return null;
        var changed = edits.GroupBy(edit => edit.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(
            group => group.Key, group => group.Aggregate(new HashSet<int>(), (lines, edit) => Update(lines, edit.Diff)), StringComparer.OrdinalIgnoreCase);
        var methods = new Dictionary<(string Path, string Member, int Start), Dictionary<int, long>>();
        foreach (var path in Reports(root, startUtc).OrderByDescending(path => File.GetLastWriteTimeUtc(path)).Take(8))
        {
            try
            {
                var full = context.Resolve(path);
                context.GuardSecrets(full);
                Collect(Load(full), root, changed, methods);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ToolException) { }
        }
        if (methods.Count == 0) return null;
        var rows = methods.OrderBy(entry => entry.Key.Path).ThenBy(entry => entry.Key.Start).Take(30).Select(entry =>
        {
            var (path, member, start) = entry.Key;
            var lines = entry.Value;
            var touched = lines.Where(line => changed[path].Contains(line.Key)).ToArray();
            return $"{Path.GetRelativePath(root, path)}:{start} · {member}: {lines.Count(line => line.Value > 0)}/{lines.Count} строк" +
                (touched.Length == 0 ? "" : $", изменённых покрыто {touched.Count(line => line.Value > 0)}/{touched.Length}");
        });
        return "Покрытие изменённых методов (Cobertura):\n" + string.Join("\n", rows);
    }

    internal static XDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 16_000_000,
        });
        var xml = XDocument.Load(reader);
        if (xml.Root?.Name.LocalName != "coverage") throw new ToolException("Ожидается отчёт Cobertura.");
        return xml;
    }

    static void Collect(XDocument xml, string root, Dictionary<string, HashSet<int>> changed,
        Dictionary<(string Path, string Member, int Start), Dictionary<int, long>> methods)
    {
        var bases = xml.Descendants("source").Select(source => source.Value).Where(Path.IsPathFullyQualified).Prepend(root).Take(20).ToArray();
        foreach (var type in xml.Descendants("class"))
        {
            var path = (string?)type.Attribute("filename");
            if (string.IsNullOrWhiteSpace(path)) continue;
            string[] matches;
            try
            {
                matches = bases.Select(folder => Path.GetFullPath(path, folder)).Where(changed.ContainsKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            if (matches.Length != 1) continue;
            var touched = changed[matches[0]];
            foreach (var method in type.Descendants("method"))
            {
                var lines = method.Descendants("line").Select(line =>
                    (Line: int.TryParse((string?)line.Attribute("number"), out int n) ? n : 0,
                    Hits: long.TryParse((string?)line.Attribute("hits"), out long hits) ? hits : 0))
                    .Where(line => line.Line > 0).GroupBy(line => line.Line).Select(group => (Line: group.Key, Hits: group.Max(line => line.Hits))).ToArray();
                if (lines.Length == 0) continue;
                int first = lines.Min(line => line.Line), last = lines.Max(line => line.Line);
                if (!touched.Any(line => line >= first && line <= last)) continue;
                string member = ((string?)type.Attribute("name") ?? "") + "." + ((string?)method.Attribute("name") ?? "?");
                var key = (matches[0], member, first);
                if (!methods.TryGetValue(key, out var hits)) methods[key] = hits = [];
                foreach (var line in lines) hits[line.Line] = Math.Max(hits.GetValueOrDefault(line.Line), line.Hits);
            }
        }
    }

    static HashSet<int> Update(HashSet<int> previous, FileDiff diff)
    {
        int old = 0, next = 0, offset = 0;
        var remaining = previous.Order().ToList();
        int cursor = 0;
        var current = new HashSet<int>();
        foreach (var row in diff.Unified.Split('\n'))
        {
            if (Hunk().Match(row) is { Success: true } header)
            {
                if (!int.TryParse(header.Groups[1].Value, out old) || !int.TryParse(header.Groups[2].Value, out next)) continue;
                while (cursor < remaining.Count && remaining[cursor] < old) current.Add(remaining[cursor++] + offset);
                continue;
            }
            if (next <= 0 || row.Length == 0) continue;
            switch (row[0])
            {
                case '+': current.Add(next++); break;
                case '-':
                    if (cursor < remaining.Count && remaining[cursor] == old) cursor++;
                    old++;
                    current.Add(next);
                    break;
                case ' ':
                    if (cursor < remaining.Count && remaining[cursor] == old) { current.Add(next); cursor++; }
                    old++;
                    next++;
                    break;
            }
            offset = next - old;
        }
        while (cursor < remaining.Count) current.Add(remaining[cursor++] + offset);
        return current;
    }

    static IEnumerable<string> Reports(string root, DateTime startUtc)
    {
        var dirs = new Queue<(string Path, int Depth, bool Reports)>([(root, 0, false)]);
        int seen = 0;
        while (dirs.TryDequeue(out var entry) && seen++ < 450)
        {
            if (!PathHelper.SamePath(entry.Path, PathHelper.FinalPath(entry.Path))) continue;
            string[] children, files;
            try
            {
                children = entry.Depth < 5 ? Directory.GetDirectories(entry.Path) : [];
                files = entry.Reports || entry.Depth == 0 ? Directory.GetFiles(entry.Path, "*cobertura*.xml") : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
                if (File.GetLastWriteTimeUtc(file) >= startUtc && PathHelper.SamePath(file, PathHelper.FinalPath(file))) yield return file;
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                bool report = name.Equals("testresults", StringComparison.OrdinalIgnoreCase) || name.Equals("coverage", StringComparison.OrdinalIgnoreCase);
                if (report || ProjectSourceIndex.Trackable(Path.GetRelativePath(root, child)))
                    dirs.Enqueue((child, entry.Depth + 1, entry.Reports || report));
            }
        }
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex Hunk();
}
