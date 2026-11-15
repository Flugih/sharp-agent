using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace SharpAgent.Ui;

public sealed partial class ComposerBox : RichTextBox
{
    private sealed record Part(string Text, TextPointer Start, TextPointer End, bool Plain, bool Link = false);

    [GeneratedRegex("""https?://[^\s<>"`]+""", RegexOptions.IgnoreCase)]
    private static partial Regex Urls();

    private readonly DispatcherTimer _links = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(650) };
    private bool _replacing;

    public ComposerBox()
    {
        IsDocumentEnabled = true;
        Document.PagePadding = new Thickness(0);
        _links.Tick += (_, _) =>
        {
            _links.Stop();
            FormatLinks();
        };
        AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(CopySelection));
        DataObject.AddPastingHandler(this, (_, e) =>
        {
            if (e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)) e.FormatToApply = DataFormats.UnicodeText;
            else e.CancelCommand();
        });
        LostKeyboardFocus += (_, _) =>
        {
            _links.Stop();
            if (IsLoaded) FormatLinks();
        };
    }

    public string Text
    {
        get => string.Concat(Parts().Select(part => part.Text));
        set
        {
            var paragraph = new Paragraph(new Run(value.ReplaceLineEndings("\n"))) { Margin = new Thickness(0) };
            BeginChange();
            try
            {
                Document.Blocks.Clear();
                Document.Blocks.Add(paragraph);
            }
            finally { EndChange(); }
        }
    }

    public int CaretIndex
    {
        get => OffsetOf(Parts(), CaretPosition);
        set => Select(value, 0);
    }

    public int SelectionStart => OffsetOf(Parts(), Selection.Start);
    public int SelectionLength => OffsetOf(Parts(), Selection.End) - SelectionStart;
    public int LineCount => Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetLineStartPosition(1) != null
        || Text.Contains('\n') ? 2 : 1;

    public string SelectedText
    {
        set => Selection.Text = value.ReplaceLineEndings("\n");
    }

    public void Clear() => Text = "";

    public void Select(int start, int length)
    {
        var parts = Parts();
        Selection.Select(PointAt(parts, start), PointAt(parts, start + length));
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        if (_replacing || _links == null) return;
        _links.Stop();
        if (e.UndoAction is not (UndoAction.Undo or UndoAction.Redo)) _links.Start();
    }

    private void CopySelection(object sender, ExecutedRoutedEventArgs e)
    {
        if (Selection.IsEmpty || e.Command != ApplicationCommands.Copy && e.Command != ApplicationCommands.Cut) return;
        var parts = Parts();
        var text = string.Concat(parts.Select(part => part.Text));
        int start = OffsetOf(parts, Selection.Start), end = OffsetOf(parts, Selection.End);
        e.Handled = true;
        if (end <= start || !TextClipboard.SetText(text[start..end])) return;
        if (e.Command == ApplicationCommands.Cut && !IsReadOnly) Selection.Text = "";
    }

    private List<Part> Parts()
    {
        var parts = new List<Part>();
        Paragraph? previous = null;
        foreach (var block in Document.Blocks)
        {
            if (block is not Paragraph paragraph) continue;
            if (previous != null) parts.Add(new Part("\n", previous.ContentEnd, paragraph.ContentStart, false));
            ReadInlines(paragraph.Inlines, parts);
            previous = paragraph;
        }
        return parts;
    }

    private static void ReadInlines(InlineCollection inlines, List<Part> parts)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run { Text.Length: > 0 } run:
                    parts.Add(new Part(run.Text, run.ContentStart, run.ContentEnd, true));
                    break;
                case LineBreak line:
                    parts.Add(new Part("\n", line.ElementStart, line.ElementEnd, false));
                    break;
                case InlineUIContainer { Tag: string raw } link:
                    parts.Add(new Part(raw, link.ElementStart, link.ElementEnd, false, Link: true));
                    break;
                case Span span:
                    ReadInlines(span.Inlines, parts);
                    break;
            }
        }
    }

    private static int OffsetOf(List<Part> parts, TextPointer point)
    {
        int offset = 0;
        foreach (var part in parts)
        {
            if (point.CompareTo(part.Start) <= 0) break;
            if (point.CompareTo(part.End) < 0)
                return offset + (part.Plain ? Math.Clamp(part.Start.GetOffsetToPosition(point), 0, part.Text.Length) : part.Text.Length);
            offset += part.Text.Length;
        }
        return offset;
    }

    private TextPointer PointAt(List<Part> parts, int index)
    {
        foreach (var part in parts)
        {
            if (index <= 0) return part.Start;
            if (index <= part.Text.Length)
                return part.Plain ? part.Start.GetPositionAtOffset(index, LogicalDirection.Forward) ?? part.End : part.End;
            index -= part.Text.Length;
        }
        return Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    private void FormatLinks()
    {
        if (_replacing) return;
        var parts = Parts();
        var text = string.Concat(parts.Select(part => part.Text));
        if (!text.Contains("http", StringComparison.OrdinalIgnoreCase)) return;
        var existing = new List<(int Start, int End)>();
        int position = 0;
        foreach (var part in parts)
        {
            if (part.Link) existing.Add((position, position + part.Text.Length));
            position += part.Text.Length;
        }
        int begin = OffsetOf(parts, Selection.Start), end = OffsetOf(parts, Selection.End);
        var matches = Urls().Matches(text).Cast<Match>().Reverse().ToArray();
        _replacing = true;
        BeginChange();
        try
        {
            foreach (var match in matches)
            {
                var raw = TrimLink(match.Value);
                int after = match.Index + raw.Length;
                if (existing.Any(item => match.Index < item.End && after > item.Start) ||
                    begin > match.Index && begin < after || end > match.Index && end < after ||
                    !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !LinkTitles.IsWebUri(uri))
                    continue;
                var range = new TextRange(PointAt(parts, match.Index), PointAt(parts, after));
                var chip = new InlineUIContainer(LinkChip(raw, uri), range.Start) { Tag = raw, BaselineAlignment = BaselineAlignment.Center };
                new TextRange(chip.ElementEnd, PointAt(Parts(), after + raw.Length)).Text = "";
                parts = Parts();
            }
            Selection.Select(PointAt(parts, begin), PointAt(parts, end));
        }
        finally
        {
            EndChange();
            _replacing = false;
        }
    }

    private static string TrimLink(string value)
    {
        var trimmed = value.TrimEnd('.', ',', ';', '!', ':', '»');
        while (trimmed.EndsWith(')') && trimmed.Count(c => c == ')') > trimmed.Count(c => c == '(') ||
               trimmed.EndsWith(']') && trimmed.Count(c => c == ']') > trimmed.Count(c => c == '['))
            trimmed = trimmed[..^1];
        return trimmed;
    }

    private FrameworkElement LinkChip(string raw, Uri uri)
    {
        var title = new TextBlock
        {
            Text = raw.Length > 48 ? raw[..45] + "…" : raw, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 225,
            Margin = new Thickness(7, 0, 1, 0),
        };
        title.SetResourceReference(ForegroundProperty, "AccentText");
        var remove = Theme.IconButton(Glyphs.Cancel, "Убрать ссылку: " + raw, 20, 9);
        var contents = new StackPanel { Orientation = Orientation.Horizontal };
        contents.Children.Add(title);
        contents.Children.Add(remove);
        var chip = new Border { Child = contents, Background = Theme.Surface2, CornerRadius = new CornerRadius(6), ToolTip = raw };
        AutomationProperties.SetName(chip, raw);
        remove.Click += (_, _) =>
        {
            var inline = FindLink(Document.Blocks.OfType<Paragraph>().SelectMany(block => block.Inlines), chip);
            if (inline == null) return;
            var caret = OffsetOf(Parts(), inline.ElementStart);
            new TextRange(inline.ElementStart, inline.ElementEnd).Text = "";
            Select(caret, 0);
            Focus();
        };
        _ = FillTitleAsync(uri, title);
        return chip;
    }

    private static InlineUIContainer? FindLink(IEnumerable<Inline> inlines, FrameworkElement chip)
    {
        foreach (var inline in inlines)
        {
            if (inline is InlineUIContainer container && ReferenceEquals(container.Child, chip)) return container;
            if (inline is Span span && FindLink(span.Inlines, chip) is { } child) return child;
        }
        return null;
    }

    private static async Task FillTitleAsync(Uri uri, TextBlock label)
    {
        if (await LinkTitles.GetAsync(uri) is { } title) label.Text = title;
    }
}
