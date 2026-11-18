using System.Windows.Documents;

namespace SharpAgent.Ui;

public sealed record MarkdownContext(PathRefs Paths, Func<string, string, Inline> FileLink, Func<string, string, Inline> WebLink,
    Func<string, string?, Block> CodeBlock);

public sealed class MarkdownRenderer(MarkdownContext context)
{
    static readonly Regex HeadingRx = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled),
        BulletRx = new(@"^[-*+•]\s+(.*)$", RegexOptions.Compiled),
        NumberedRx = new(@"^(\d{1,4})[.)]\s+(.*)$", RegexOptions.Compiled),
        RuleRx = new(@"^((\*\s*){3,}|(-\s*){3,}|(_\s*){3,})$", RegexOptions.Compiled),
        TableSeparatorRx = new(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$", RegexOptions.Compiled);

    public FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new(0), FontFamily = Theme.Ui, FontSize = 14, LineHeight = 21, TextAlignment = TextAlignment.Left,
        }.Paint(TextElement.ForegroundProperty, "Text");
        var blocks = doc.Blocks;
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        Paragraph? open = null;
        bool quoted = false;
        for (int i = 0, next; i < lines.Length; i = next)
        {
            var t = lines[i].Trim();
            next = i + 1;
            if (IsFence(t)) next = ParseFence(lines, i, blocks, t, 0);
            else if (HeadingRx.Match(t) is { Success: true } heading)
            {
                int level = heading.Groups[1].Length;
                var text = heading.Groups[2].Value;
                blocks.Add(WithText(new Paragraph
                {
                    FontSize = level switch { 1 => 20, 2 => 16, _ => 14 }, FontWeight = FontWeights.SemiBold, Margin = new(0, level <= 2 ? 12 : 8, 0, 8),
                }, text.TrimEnd('#').TrimEnd() is { Length: > 0 } title ? title : text[..1]));
            }
            else if (RuleRx.IsMatch(t))
                blocks.Add(new Paragraph { BorderThickness = new(0, 0, 0, 1), Margin = new(0, 4, 0, 16), FontSize = 2, LineHeight = 2 }.Paint(Block.BorderBrushProperty, "LineStrong"));
            else if (IsListItem(t)) next = ParseList(lines, i, blocks);
            else if (t.StartsWith('|') && next < lines.Length && TableSeparatorRx.IsMatch(lines[next].Trim())) next = ParseTable(lines, i, blocks);
            else if (t.Length > 0)
            {
                bool quote = t.StartsWith('>');
                if (open == null || quoted != quote)
                    blocks.Add(open = quote
                        ? new Paragraph
                        {
                            BorderThickness = new(2, 0, 0, 0), Padding = new(12, 0, 0, 0), Margin = new(0, 0, 0, 12),
                        }.Paint(TextElement.ForegroundProperty, "TextMuted").Paint(Block.BorderBrushProperty, "LineStrong")
                        : new Paragraph { Margin = new(0, 0, 0, 12) });
                else open.Inlines.Add(new LineBreak());
                quoted = quote;
                AddInlines(open.Inlines, quote ? t.TrimStart('>').Trim() : t);
                continue;
            }
            open = null;
        }
        if (blocks.LastBlock is { } last) last.Margin = last.Margin with { Bottom = 0 };
        return doc;
    }

    Paragraph WithText(Paragraph paragraph, string text)
    {
        AddInlines(paragraph.Inlines, text);
        return paragraph;
    }

    int ParseFence(string[] lines, int start, BlockCollection target, string opener, int indent)
    {
        var fence = opener[..3];
        var language = opener[3..].Trim().Split(' ', '\t')[0];
        var code = lines.Skip(start + 1).TakeWhile(line => !line.Trim().StartsWith(fence)).Select(line => Unindent(line, indent)).ToList();
        target.Add(context.CodeBlock(string.Join("\n", code), language.Length > 0 ? language : null));
        return start + code.Count + 2;
    }

    static bool IsFence(string trimmed) => trimmed.StartsWith("~~~") || (trimmed.StartsWith("```") && !trimmed.TrimStart('`').Contains('`'));

    static bool IsListItem(string trimmed) => BulletRx.IsMatch(trimmed) || NumberedRx.IsMatch(trimmed);

    static int Indent(string line) => line.TakeWhile(c => c is ' ' or '\t').Sum(c => c == '\t' ? 4 : 1);

    static string Unindent(string line, int indent)
    {
        int cut = 0, width = 0;
        while (cut < line.Length && line[cut] is ' ' or '\t' && (width += line[cut] == '\t' ? 4 : 1) <= indent) cut++;
        return line[cut..];
    }

    int ParseList(string[] lines, int start, BlockCollection target)
    {
        int baseIndent = Indent(lines[start]), i = start;
        var first = NumberedRx.Match(lines[start].Trim());
        bool ordered = first.Success;
        var list = new List { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new(0, 0, 0, 12), Padding = new(24, 0, 0, 0) };
        if (ordered && int.TryParse(first.Groups[1].Value, out var startIndex)) list.StartIndex = Math.Max(1, startIndex);
        while (i < lines.Length)
        {
            var t = lines[i].Trim();
            int indent = Indent(lines[i]);
            var last = list.ListItems.LastListItem;
            if (t.Length == 0)
            {
                var following = lines.ElementAtOrDefault(i + 1) ?? "";
                if (!(IsListItem(following.Trim()) && Indent(following) >= baseIndent || IsFence(following.Trim()) && Indent(following) > baseIndent)) break;
                i++;
            }
            else if (!IsListItem(t))
            {
                if (indent <= baseIndent || last == null || !IsFence(t) && last.Blocks.LastBlock is List) break;
                if (IsFence(t)) i = ParseFence(lines, i, last.Blocks, t, indent);
                else
                {
                    if (last.Blocks.LastBlock is Paragraph previous) previous.Inlines.Add(new LineBreak());
                    else last.Blocks.Add(previous = new Paragraph { Margin = new(0, 0, 0, 4) });
                    AddInlines(previous.Inlines, t);
                    i++;
                }
            }
            else if (indent < baseIndent) break;
            else if (indent >= baseIndent + 2 && last != null) i = ParseList(lines, i, last.Blocks);
            else
            {
                var numbered = NumberedRx.Match(t);
                if (numbered.Success != ordered) break;
                var content = numbered.Success ? numbered.Groups[2].Value : BulletRx.Match(t).Groups[1].Value;
                if (content.StartsWith("[ ] ", StringComparison.Ordinal)) content = "☐ " + content[4..];
                else if (content.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) content = "☑ " + content[4..];
                var p = new Paragraph { Margin = new(0, 0, 0, 4) };
                var item = new ListItem(p);
                list.ListItems.Add(item);
                if (IsFence(content)) i = ParseFence(lines, i, item.Blocks, content, indent + t.Length - content.Length);
                else
                {
                    AddInlines(p.Inlines, content);
                    i++;
                }
            }
        }
        target.Add(list);
        return i;
    }

    int ParseTable(string[] lines, int start, BlockCollection target)
    {
        var rows = lines.Skip(start + 2).TakeWhile(line => line.Trim().StartsWith('|')).Select(SplitRow).Prepend(SplitRow(lines[start])).ToList();
        int columns = rows.Max(row => row.Length);
        var table = new Table { CellSpacing = 0, Margin = new(0, 0, 0, 12), BorderThickness = new(1, 1, 0, 0) }.Paint(Block.BorderBrushProperty, "LineStrong");
        for (int c = 0; c < columns; c++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            if (r == 0) row.Paint(TextElement.BackgroundProperty, "Surface2");
            for (int c = 0; c < columns; c++)
            {
                var p = new Paragraph { Margin = new(0), FontSize = 13 };
                if (r == 0) p.FontWeight = FontWeights.SemiBold;
                row.Cells.Add(new TableCell(WithText(p, rows[r].ElementAtOrDefault(c) ?? ""))
                {
                    Padding = new(8, 4, 8, 4), BorderThickness = new(0, 0, 1, 1),
                }.Paint(TableCell.BorderBrushProperty, "LineStrong"));
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        target.Add(table);
        return start + rows.Count + 1;
    }

    static string[] SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        return t.Split('|', StringSplitOptions.TrimEntries);
    }

    void AddInlines(InlineCollection inlines, string text)
    {
        int from = 0, i = 0, end;
        void Add(Inline? inline, int next)
        {
            if (i > from) inlines.Add(new Run(text[from..i]));
            if (inline != null) inlines.Add(inline);
            from = i = next;
        }
        Span Nested(Span span, int start, int stop)
        {
            AddInlines(span.Inlines, text[start..stop]);
            return span;
        }
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '`' && (end = text.IndexOf('`', i + 1)) > i + 1) Add(Code(text[(i + 1)..end]), end + 1);
            else if ((c == '[' || c == '!' && i + 1 < text.Length && text[i + 1] == '[') &&
                     ParseLink(text, c == '!' ? i + 1 : i) is (var label, var target, var next) && LooksLikeLinkTarget(target))
            {
                var link = Link(label, target);
                Add(link, next);
                if (link == null) AddInlines(inlines, label);
            }
            else if (c is '*' or '_' && i + 1 < text.Length && text[i + 1] == c &&
                     (end = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal)) > i + 2 && !char.IsWhiteSpace(text[i + 2]))
                Add(Nested(new Span { FontWeight = FontWeights.SemiBold }, i + 2, end), end + 2);
            else if (c is '*' or '_' && (end = EmphasisEnd(text, i)) > 0) Add(Nested(new Span { FontStyle = FontStyles.Italic }, i + 1, end), end + 1);
            else if (c == 'h' && StartsWordAt(text, i) && (end = UrlLength(text, i)) > 0)
            {
                var url = text.Substring(i, end);
                Add(context.WebLink(url, url), i + end);
            }
            else if (char.IsAsciiLetter(c) && i + 2 < text.Length && text[i + 1] == ':' && text[i + 2] is '\\' or '/' && StartsWordAt(text, i))
            {
                if (context.Paths.MatchBarePath(text, i) is { } hit) Add(context.FileLink(hit.FullPath, text.Substring(i, hit.Length)), i + hit.Length);
                else i += Math.Max(1, PathRefs.PlainTokenLength(text, i));
            }
            else i++;
        }
        Add(null, i);
    }

    Inline? FileLinkTo(string candidate, string text) =>
        context.Paths.Resolve(candidate, allowRelative: true) is { } path ? context.FileLink(path, text) : null;

    Inline Code(string code) =>
        FileLinkTo(code, code.Trim()) ?? new Run(" " + code + " ") { FontFamily = Theme.Mono, FontSize = 13 }.Paint(TextElement.BackgroundProperty, "Surface3").Paint(TextElement.ForegroundProperty, "Text");

    Inline? Link(string label, string target) => IsWebUrl(target) ? context.WebLink(target, label)
        : FileLinkTo(Starts(target, "file:") && Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile && !uri.IsUnc ? uri.LocalPath
            : target.Contains('%') ? Uri.UnescapeDataString(target) : target, label);

    static bool LooksLikeLinkTarget(string target) =>
        target.AsSpan().ContainsAny(@"\/:") || target[0] is '#' or '~' or '%' || Path.GetExtension(target).Length is > 1 and <= 11;

    static bool Starts(string text, string prefix) => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    static bool IsWebUrl(string target) => Starts(target, "http://") || Starts(target, "https://") || Starts(target, "mailto:");

    static (string Label, string Target, int Next)? ParseLink(string text, int open)
    {
        int close = text.IndexOf(']', open + 1), start = close + 2, end, next;
        if (close <= open + 1 || close + 1 >= text.Length || text[close + 1] != '(') return null;
        string target;
        if (start < text.Length && text[start] == '<')
        {
            if ((end = text.IndexOf('>', start + 1)) < 0) return null;
            next = end + 1;
            while (next < text.Length && text[next] == ' ') next++;
            if (next >= text.Length || text[next] != ')') return null;
            target = text[(start + 1)..end].Trim();
        }
        else
        {
            int depth = 1;
            for (next = start; next < text.Length; next++)
                if (text[next] == '(') depth++;
                else if (text[next] == ')' && --depth == 0) break;
            if (next >= text.Length) return null;
            target = text[start..next].Trim();
            if (IsWebUrl(target) && target.IndexOf(' ') is > 0 and var space) target = target[..space];
        }
        var label = text[(open + 1)..close];
        return target.Length > 0 && label.Trim().Length > 0 ? (label, target, next + 1) : null;
    }

    static int EmphasisEnd(string text, int open)
    {
        char mark = text[open];
        if (Joins(text, open - 1, mark) || open + 1 >= text.Length || char.IsWhiteSpace(text[open + 1]) || text[open + 1] == mark) return 0;
        int close = text.IndexOf(mark, open + 1);
        return close < 0 || char.IsWhiteSpace(text[close - 1]) || Joins(text, close + 1, mark) ? 0 : close;
    }

    static bool Joins(string text, int index, char mark) =>
        index >= 0 && index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == mark);

    static bool StartsWordAt(string text, int index) =>
        index == 0 || !(char.IsLetterOrDigit(text[index - 1]) || text[index - 1] is '/' or '\\' or '.' or ':' or '@' or '-' or '_');

    static int UrlLength(string text, int start)
    {
        var rest = text.AsSpan(start);
        int scheme = rest.StartsWith("https://") ? 8 : rest.StartsWith("http://") ? 7 : 0;
        if (scheme == 0) return 0;
        int end = rest.IndexOfAny(" \t<>\"`\u00A0") is >= 0 and var stop ? start + stop : text.Length;
        while (end > start + scheme && text[end - 1] is var last &&
               (".,;:!?»\"'…".Contains(last) || last is ')' or ']' && text.IndexOf(last == ')' ? '(' : '[', start, end - start) < 0))
            end--;
        return end - start > scheme ? end - start : 0;
    }
}
