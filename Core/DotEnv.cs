using System.Text;

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
        var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
        var pending = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);

        foreach (bool commentedPass in new[] { false, true })
        {
            for (int i = 0; i < lines.Count && pending.Count > 0; i++)
            {
                var t = lines[i].Trim();
                bool commented = t.StartsWith('#');
                if (commented != commentedPass) continue;
                if (!TrySplit(commented ? t.TrimStart('#').Trim() : t, out var key, out _)) continue;
                if (!pending.TryGetValue(key, out var value)) continue;
                if (commented && string.IsNullOrEmpty(value)) continue;

                lines[i] = $"{key}={Quote(value)}";
                pending.Remove(key);
            }
        }

        foreach (var (key, value) in pending)
            if (!string.IsNullOrEmpty(value)) lines.Add($"{key}={Quote(value)}");

        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static bool TrySplit(string line, out string key, out string value)
    {
        if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
        int eq = line.IndexOf('=');
        key = eq > 0 ? line[..eq].Trim() : "";
        value = line[(eq + 1)..].Trim();
        return eq > 0;
    }

    private static string Quote(string? value)
    {
        value ??= "";
        bool needsQuotes = value.Contains(" #") || value != value.Trim() || value.StartsWith('"') || value.StartsWith('\'');
        return needsQuotes ? $"'{value}'" : value;
    }
}
