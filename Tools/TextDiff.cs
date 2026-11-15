using System.Text;

namespace SharpAgent.Tools;

public sealed record FileDiff(string Unified, int Added, int Removed);

public static class TextDiff
{
    public const string Truncated = "…";
    private const int Context = 3, MaxLines = 800;
    private const long MaxGrid = 1_000_000;

    public static FileDiff? Compare(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);
        int head = 0, tail = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)]) tail++;
        if (head + tail == a.Length && head + tail == b.Length) return null;

        var ops = new List<(char Kind, string Text)>();
        ops.AddRange(a[..head].Select(line => (' ', line)));
        AddMiddle(a[head..^tail], b[head..^tail], ops);
        ops.AddRange(a[^tail..].Select(line => (' ', line)));
        return Format(ops);
    }

    private static string[] Lines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static void AddMiddle(string[] a, string[] b, List<(char Kind, string Text)> ops)
    {
        if ((long)a.Length * b.Length > MaxGrid)
        {
            ops.AddRange(a.Select(line => ('-', line)));
            ops.AddRange(b.Select(line => ('+', line)));
            return;
        }

        var common = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                common[i, j] = a[i] == b[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);

        for (int x = 0, y = 0; x < a.Length || y < b.Length;)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                ops.Add((' ', a[x]));
                x++;
                y++;
            }
            else if (x < a.Length && (y == b.Length || common[x + 1, y] >= common[x, y + 1])) ops.Add(('-', a[x++]));
            else ops.Add(('+', b[y++]));
        }
    }

    private static FileDiff Format(List<(char Kind, string Text)> ops)
    {
        var oldAt = new int[ops.Count + 1];
        var newAt = new int[ops.Count + 1];
        oldAt[0] = newAt[0] = 1;
        for (int i = 0; i < ops.Count; i++)
        {
            oldAt[i + 1] = oldAt[i] + (ops[i].Kind == '+' ? 0 : 1);
            newAt[i + 1] = newAt[i] + (ops[i].Kind == '-' ? 0 : 1);
        }

        var changes = Enumerable.Range(0, ops.Count).Where(i => ops[i].Kind != ' ').ToList();
        int added = changes.Count(i => ops[i].Kind == '+');
        var text = new StringBuilder();
        int shown = 0;
        for (int c = 0; c < changes.Count; c++)
        {
            int first = changes[c], last = first;
            while (c + 1 < changes.Count && changes[c + 1] - last <= 2 * Context + 1) last = changes[++c];
            int from = Math.Max(0, first - Context), to = Math.Min(ops.Count, last + Context + 1);
            text.Append($"@@ -{oldAt[from]},{oldAt[to] - oldAt[from]} +{newAt[from]},{newAt[to] - newAt[from]} @@\n");
            for (int i = from; i < to; i++)
            {
                if (shown++ == MaxLines) return new FileDiff(text.Append(Truncated).ToString(), added, changes.Count - added);
                text.Append(ops[i].Kind).Append(ops[i].Text).Append('\n');
            }
        }
        return new FileDiff(text.ToString(), added, changes.Count - added);
    }
}
