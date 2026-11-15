using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace SharpAgent.Ui;

public sealed class MarkdownContext
{
    public required PathRefs Paths { get; init; }
    public required Func<string, string, Inline> FileLink { get; init; }
    public required Func<string, string, Inline> WebLink { get; init; }
    public required Func<string, string?, Block> CodeBlock { get; init; }
}

public sealed class MarkdownRenderer(MarkdownContext context)
{
    private static readonly Regex HeadingRx = new(@"^(#{1,6})\s+(.+?)\s*#*$", RegexOptions.Compiled);
    private static readonly Regex BulletRx = new(@"^[-*+•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberedRx = new(@"^(\d{1,4})[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RuleRx = new(@"^((\*\s*){3,}|(-\s*){3,}|(_\s*){3,})$", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorRx = new(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$", RegexOptions.Compiled);
    private static readonly char[] UrlEnd = [' ', '\t', '<', '>', '"', '`', '\u00A0'];
    private static readonly char[] UrlTrailing = ['.', ',', ';', ':', '!', '?', '»', '"', '\'', '…'];

    public FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = Theme.Ui,
            FontSize = 14,
            LineHeight = 21,
            Foreground = Theme.Text,
            TextAlignment = TextAlignment.Left,
        };

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        Paragraph? paragraph = null;
        int i = 0;
        while (i < lines.Length)
        {
            var t = lines[i].Trim();
            int next = i + 1;
            if (IsFence(t)) next = ParseFence(lines, i, doc.Blocks, t, 0);
            else if (HeadingRx.Match(t) is { Success: true } heading)
                doc.Blocks.Add(HeadingBlock(heading.Groups[2].Value, heading.Groups[1].Length));
            else if (RuleRx.IsMatch(t)) doc.Blocks.Add(RuleBlock());
            else if (IsListItem(t)) next = ParseList(lines, i, doc.Blocks);
            else if (t.StartsWith('|') && i + 1 < lines.Length && TableSeparatorRx.IsMatch(lines[i + 1].Trim()))
                next = ParseTable(lines, i, doc.Blocks);
            else if (t.StartsWith('>')) next = ParseQuote(lines, i, doc.Blocks);
            else if (t.Length > 0)
            {
                if (paragraph == null) doc.Blocks.Add(paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) });
                else paragraph.Inlines.Add(new LineBreak());
                AddInlines(paragraph.Inlines, t);
                i++;
                continue;
            }
            paragraph = null;
            i = next;
        }

        if (doc.Blocks.LastBlock is { } last) last.Margin = last.Margin with { Bottom = 0 };
        return doc;
    }

    private Paragraph HeadingBlock(string text, int level)
    {
        var p = new Paragraph
        {
            FontSize = level switch { 1 => 20, 2 => 16, _ => 14 },
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, level <= 2 ? 12 : 8, 0, 8),
        };
        AddInlines(p.Inlines, text);
        return p;
    }

    private int ParseFence(string[] lines, int start, BlockCollection target, string opener, int indent)
    {
        var fence = opener[..3];
        var language = opener[3..].Trim().Split(' ', '\t')[0];
        var code = new List<string>();
        int i = start + 1;
        while (i < lines.Length && !lines[i].Trim().StartsWith(fence)) code.Add(Unindent(lines[i++], indent));
        target.Add(context.CodeBlock(string.Join("\n", code), language.Length > 0 ? language : null));
        return i + 1;
    }

    private int ParseQuote(string[] lines, int start, BlockCollection target)
    {
        var p = new Paragraph
        {
            Foreground = Theme.TextMuted,
            BorderBrush = Theme.LineStrong,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 12),
        };
        int i = start;
        while (i < lines.Length && lines[i].Trim().StartsWith('>'))
        {
            if (i > start) p.Inlines.Add(new LineBreak());
            AddInlines(p.Inlines, lines[i++].Trim().TrimStart('>').Trim());
        }
        target.Add(p);
        return i;
    }

    private static Paragraph RuleBlock() => new()
    {
        BorderBrush = Theme.LineStrong,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Margin = new Thickness(0, 4, 0, 16),
        FontSize = 2,
        LineHeight = 2,
    };

    private static bool IsFence(string trimmed) =>
        trimmed.StartsWith("~~~") || (trimmed.StartsWith("```") && !trimmed.TrimStart('`').Contains('`'));

    private static bool IsListItem(string trimmed) => BulletRx.IsMatch(trimmed) || NumberedRx.IsMatch(trimmed);

    private static int Indent(string line) => line.TakeWhile(c => c is ' ' or '\t').Sum(c => c == '\t' ? 4 : 1);

    private static string Unindent(string line, int indent)
    {
        int cut = 0;
        while (cut < line.Length && line[cut] is ' ' or '\t' && Indent(line[..(cut + 1)]) <= indent) cut++;
        return line[cut..];
    }

    private int ParseList(string[] lines, int start, BlockCollection target)
    {
        int baseIndent = Indent(lines[start]);
        var first = NumberedRx.Match(lines[start].Trim());
        bool ordered = first.Success;
        var list = new List
        {
            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(24, 0, 0, 0),
        };
        if (ordered && int.TryParse(first.Groups[1].Value, out var startIndex)) list.StartIndex = Math.Max(1, startIndex);

        int i = start;
        while (i < lines.Length)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0)
            {
                var following = i + 1 < lines.Length ? lines[i + 1] : "";
                if ((IsListItem(following.Trim()) && Indent(following) >= baseIndent) ||
                    (IsFence(following.Trim()) && Indent(following) > baseIndent))
                {
                    i++;
                    continue;
                }
                break;
            }

            int indent = Indent(line);
            var last = list.ListItems.LastListItem;
            if (!IsListItem(t))
            {
                if (indent <= baseIndent || last == null) break;
                if (IsFence(t))
                {
                    i = ParseFence(lines, i, last.Blocks, t, indent);
                    continue;
                }
                if (last.Blocks.LastBlock is List) break;
                if (last.Blocks.LastBlock is Paragraph previous) previous.Inlines.Add(new LineBreak());
                else last.Blocks.Add(previous = new Paragraph { Margin = new Thickness(0, 0, 0, 4) });
                AddInlines(previous.Inlines, t);
                i++;
                continue;
            }
            if (indent < baseIndent) break;
            if (indent >= baseIndent + 2 && last != null)
            {
                i = ParseList(lines, i, last.Blocks);
                continue;
            }

            var numbered = NumberedRx.Match(t);
            if (numbered.Success != ordered) break;
            var content = numbered.Success ? numbered.Groups[2].Value : BulletRx.Match(t).Groups[1].Value;

            if (content.StartsWith("[ ] ", StringComparison.Ordinal)) content = "☐ " + content[4..];
            else if (content.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) content = "☑ " + content[4..];

            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
            var item = new ListItem(p);
            list.ListItems.Add(item);
            if (IsFence(content))
            {
                i = ParseFence(lines, i, item.Blocks, content, indent + t.Length - content.Length);
                continue;
            }
            AddInlines(p.Inlines, content);
            i++;
        }

        target.Add(list);
        return i;
    }

    private int ParseTable(string[] lines, int start, BlockCollection target)
    {
        var header = SplitRow(lines[start]);
        int i = start + 2;
        var rows = new List<string[]>();
        while (i < lines.Length && lines[i].Trim().StartsWith('|')) rows.Add(SplitRow(lines[i++]));
        int columns = rows.Prepend(header).Max(r => r.Length);

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 12),
            BorderBrush = Theme.LineStrong,
            BorderThickness = new Thickness(1, 1, 0, 0),
        };
        for (int c = 0; c < columns; c++) table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        group.Rows.Add(MakeRow(header, columns, isHeader: true));
        foreach (var row in rows) group.Rows.Add(MakeRow(row, columns, isHeader: false));
        table.RowGroups.Add(group);
        target.Add(table);
        return i;
    }

    private static string[] SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|', StringSplitOptions.TrimEntries);
    }

    private TableRow MakeRow(string[] cells, int columns, bool isHeader)
    {
        var row = new TableRow();
        if (isHeader) row.Background = Theme.Surface2;
        for (int c = 0; c < columns; c++)
        {
            var p = new Paragraph { Margin = new Thickness(0), FontSize = 13 };
            if (isHeader) p.FontWeight = FontWeights.SemiBold;
            AddInlines(p.Inlines, c < cells.Length ? cells[c] : "");
            row.Cells.Add(new TableCell(p)
            {
                Padding = new Thickness(8, 4, 8, 4),
                BorderBrush = Theme.LineStrong,
                BorderThickness = new Thickness(0, 0, 1, 1),
            });
        }
        return row;
    }

    private void AddInlines(InlineCollection inlines, string text)
    {
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            inlines.Add(new Run(plain.ToString()));
            plain.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    Flush();
                    AddCode(inlines, text[(i + 1)..close]);
                    i = close + 1;
                    continue;
                }
            }
            else if (c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '['))
            {
                int open = c == '!' ? i + 1 : i;
                if (TryParseLink(text, open, out var label, out var target, out int next) && LooksLikeLinkTarget(target))
                {
                    Flush();
                    AddLink(inlines, label, target);
                    i = next;
                    continue;
                }
            }
            else if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                int close = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                if (close > i + 2 && !char.IsWhiteSpace(text[i + 2]))
                {
                    Flush();
                    var bold = new Span { FontWeight = FontWeights.SemiBold };
                    AddInlines(bold.Inlines, text[(i + 2)..close]);
                    inlines.Add(bold);
                    i = close + 2;
                    continue;
                }
            }
            else if (c == '*' || c == '_')
            {
                int close = EmphasisEnd(text, i);
                if (close > 0)
                {
                    Flush();
                    var italic = new Span { FontStyle = FontStyles.Italic };
                    AddInlines(italic.Inlines, text[(i + 1)..close]);
                    inlines.Add(italic);
                    i = close + 1;
                    continue;
                }
            }
            else if (c == 'h' && StartsWordAt(text, i) && UrlLength(text, i) is > 0 and var urlLength)
            {
                Flush();
                var url = text.Substring(i, urlLength);
                inlines.Add(context.WebLink(url, url));
                i += urlLength;
                continue;
            }
            else if (char.IsAsciiLetter(c) && i + 2 < text.Length && text[i + 1] == ':' &&
                     (text[i + 2] == '\\' || text[i + 2] == '/') && StartsWordAt(text, i))
            {
                if (context.Paths.MatchBarePath(text, i) is { } hit)
                {
                    Flush();
                    inlines.Add(context.FileLink(hit.FullPath, text.Substring(i, hit.Length)));
                    i += hit.Length;
                    continue;
                }
                int length = Math.Max(1, PathRefs.PlainTokenLength(text, i));
                plain.Append(text, i, length);
                i += length;
                continue;
            }

            plain.Append(c);
            i++;
        }
        Flush();
    }

    private void AddCode(InlineCollection inlines, string code)
    {
        if (context.Paths.Resolve(code, allowRelative: true) is { } path)
        {
            inlines.Add(context.FileLink(path, code.Trim()));
            return;
        }
        inlines.Add(new Run(" " + code + " ")
        {
            FontFamily = Theme.Mono,
            FontSize = 13,
            Background = Theme.Surface3,
            Foreground = Theme.Text,
        });
    }

    private void AddLink(InlineCollection inlines, string label, string target)
    {
        if (IsWebUrl(target))
        {
            inlines.Add(context.WebLink(target, label));
            return;
        }

        var local = target;
        if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile && !uri.IsUnc)
            local = uri.LocalPath;
        else if (target.Contains('%'))
            local = Uri.UnescapeDataString(target);

        if (context.Paths.Resolve(local, allowRelative: true) is { } path) inlines.Add(context.FileLink(path, label));
        else AddInlines(inlines, label);
    }

    private static bool LooksLikeLinkTarget(string target) =>
        IsWebUrl(target) || target.Contains('\\') || target.Contains('/') || target.Contains(':') ||
        target.StartsWith('#') || target.StartsWith('~') || target.StartsWith('%') ||
        Path.GetExtension(target).Length is > 1 and <= 11;

    private static bool IsWebUrl(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseLink(string text, int open, out string label, out string target, out int next)
    {
        label = target = "";
        next = open;
        int close = text.IndexOf(']', open + 1);
        if (close <= open + 1 || close + 1 >= text.Length || text[close + 1] != '(') return false;

        int start = close + 2;
        if (start < text.Length && text[start] == '<')
        {
            int end = text.IndexOf('>', start + 1);
            if (end < 0) return false;
            int after = end + 1;
            while (after < text.Length && text[after] == ' ') after++;
            if (after >= text.Length || text[after] != ')') return false;
            target = text[(start + 1)..end].Trim();
            next = after + 1;
        }
        else
        {
            int depth = 1, pos = start;
            for (; pos < text.Length; pos++)
            {
                if (text[pos] == '(') depth++;
                else if (text[pos] == ')' && --depth == 0) break;
            }
            if (pos >= text.Length) return false;
            target = text[start..pos].Trim();
            next = pos + 1;

            if (IsWebUrl(target) && target.IndexOf(' ') is > 0 and var space) target = target[..space];
        }

        label = text[(open + 1)..close];
        return target.Length > 0 && label.Trim().Length > 0;
    }

    private static int EmphasisEnd(string text, int open)
    {
        char mark = text[open];
        if (open > 0 && (char.IsLetterOrDigit(text[open - 1]) || text[open - 1] == mark)) return 0;
        if (open + 1 >= text.Length || char.IsWhiteSpace(text[open + 1]) || text[open + 1] == mark) return 0;

        int close = text.IndexOf(mark, open + 1);
        if (close <= open + 1 || char.IsWhiteSpace(text[close - 1])) return 0;
        if (close + 1 < text.Length && (char.IsLetterOrDigit(text[close + 1]) || text[close + 1] == mark)) return 0;
        return close;
    }

    private static bool StartsWordAt(string text, int index) =>
        index == 0 || !(char.IsLetterOrDigit(text[index - 1]) || text[index - 1] is '/' or '\\' or '.' or ':' or '@' or '-' or '_');

    private static int UrlLength(string text, int start)
    {
        int scheme = string.CompareOrdinal(text, start, "https://", 0, 8) == 0 ? 8
            : string.CompareOrdinal(text, start, "http://", 0, 7) == 0 ? 7
            : 0;
        if (scheme == 0) return 0;

        int end = text.IndexOfAny(UrlEnd, start);
        if (end < 0) end = text.Length;
        while (end > start + scheme)
        {
            char last = text[end - 1];
            bool punctuation = Array.IndexOf(UrlTrailing, last) >= 0;
            bool strayBracket = last is ')' or ']' && text.IndexOf(last == ')' ? '(' : '[', start, end - start) < 0;
            if (!punctuation && !strayBracket) break;
            end--;
        }
        return end - start > scheme ? end - start : 0;
    }
}
