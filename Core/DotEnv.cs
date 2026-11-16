namespace SharpAgent.Core;

public static class DotEnv
{
    public const string FileName = ".env";

    public static Dictionary<string, string> Parse(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#') || !TrySplit(line, out var key, out var value)) continue;
            result[key] = value switch
            {
                ['"', .. var inner, '"'] => inner.Replace("\\n", "\n").Replace("\\\"", "\""),
                ['\'', .. var inner, '\''] => inner,
                _ => value.Split(" #")[0].TrimEnd(),
            };
        }
        return result;
    }

    public static void Save(string path, IReadOnlyDictionary<string, string?> values)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : [];
        var pending = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        foreach (bool commentedPass in new[] { false, true })
            for (int n = 0; n < lines.Count && pending.Count > 0; n++)
            {
                int i = commentedPass ? n : lines.Count - 1 - n;
                var t = lines[i].Trim();
                bool commented = t.StartsWith('#');
                if (commented != commentedPass || !TrySplit(commented ? t.TrimStart('#').Trim() : t, out var key, out _) ||
                    !pending.TryGetValue(key, out var value) || commented && string.IsNullOrEmpty(value)) continue;
                lines[i] = $"{key}={Quote(value)}";
                pending.Remove(key);
            }
        foreach (var (key, value) in pending)
            if (!string.IsNullOrEmpty(value)) lines.Add($"{key}={Quote(value)}");
        var temp = path + ".tmp";
        try
        {
            File.WriteAllLines(temp, lines, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    static bool TrySplit(string line, out string key, out string value)
    {
        if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
        int eq = line.IndexOf('=');
        key = eq > 0 ? line[..eq].Trim() : "";
        value = line[(eq + 1)..].Trim();
        return eq > 0;
    }

    static string Quote(string? value)
    {
        value ??= "";
        if (value.Contains('\n') || value.Contains('\r')) return '"' + value.Replace("\"", "\\\"").ReplaceLineEndings("\\n") + '"';
        return value.Contains(" #") || value != value.Trim() || value.StartsWith('"') || value.StartsWith('\'') ? $"'{value}'" : value;
    }
}
