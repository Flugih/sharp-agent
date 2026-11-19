using System.Windows.Documents;

namespace SharpAgent.Ui;

public sealed partial class ComposerBox : RichTextBox
{
    sealed record Part(string Text, TextPointer Start, TextPointer End, bool Plain, bool Link = false);

    [GeneratedRegex("""https?://[^\s<>"`]+""", RegexOptions.IgnoreCase)]
    private static partial Regex Urls();

    readonly DispatcherTimer _links = new(DispatcherPriority.ContextIdle) { Interval = TimeSpan.FromMilliseconds(650) };
    bool _replacing;
    string? _text;

    public ComposerBox()
    {
        IsDocumentEnabled = true;
        Document.PagePadding = new(0);
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
    }

    public string Text
    {
        get => _text ??= Join(Parts());
        set
        {
            var normalized = value.ReplaceLineEndings("\n");
            if (Text == normalized) return;
            var paragraph = new Paragraph(new Run(normalized)) { Margin = new(0) };
            using (DeclareChangeBlock())
            {
                Document.Blocks.Clear();
                Document.Blocks.Add(paragraph);
            }
        }
    }

    public int CaretIndex { get => OffsetOf(Parts(), CaretPosition); set => Select(value, 0); }

    public int SelectionStart => OffsetOf(Parts(), Selection.Start);
    public int SelectionLength => OffsetOf(Parts(), Selection.End) - SelectionStart;
    public int LineCount =>
        Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetLineStartPosition(1) != null || Text.Contains('\n') ? 2 : 1;

    public string SelectedText { set => Selection.Text = value.ReplaceLineEndings("\n"); }

    public void Clear() => Text = "";

    public void Select(int start, int length)
    {
        var parts = Parts();
        Selection.Select(PointAt(parts, start), PointAt(parts, start + length));
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        _text = null;
        base.OnTextChanged(e);
        if (_replacing) return;
        _links.Stop();
        if (e.UndoAction is not (UndoAction.Undo or UndoAction.Redo)) _links.Start();
    }

    void CopySelection(object sender, ExecutedRoutedEventArgs e)
    {
        if (Selection.IsEmpty || e.Command != ApplicationCommands.Copy && e.Command != ApplicationCommands.Cut) return;
        var parts = Parts();
        var text = Join(parts);
        int start = OffsetOf(parts, Selection.Start), end = OffsetOf(parts, Selection.End);
        e.Handled = true;
        if (end <= start || !TextClipboard.SetText(text[start..end])) return;
        if (e.Command == ApplicationCommands.Cut && !IsReadOnly) Selection.Text = "";
    }

    static string Join(List<Part> parts) => string.Concat(parts.Select(part => part.Text));

    List<Part> Parts()
    {
        var parts = new List<Part>();
        Paragraph? previous = null;
        foreach (var paragraph in Document.Blocks.OfType<Paragraph>())
        {
            if (previous != null) parts.Add(new("\n", previous.ContentEnd, paragraph.ContentStart, false));
            ReadInlines(paragraph.Inlines, parts);
            previous = paragraph;
        }
        return parts;
    }

    static void ReadInlines(InlineCollection inlines, List<Part> parts)
    {
        foreach (var inline in inlines)
            switch (inline)
            {
                case Run { Text.Length: > 0 } run:
                    parts.Add(new(run.Text, run.ContentStart, run.ContentEnd, true));
                    break;
                case LineBreak line:
                    parts.Add(new("\n", line.ElementStart, line.ElementEnd, false));
                    break;
                case InlineUIContainer { Tag: string raw } link:
                    parts.Add(new(raw, link.ElementStart, link.ElementEnd, false, Link: true));
                    break;
                case Span span:
                    ReadInlines(span.Inlines, parts);
                    break;
            }
    }

    static int OffsetOf(List<Part> parts, TextPointer point)
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

    TextPointer PointAt(List<Part> parts, int index)
    {
        foreach (var part in parts)
        {
            if (index <= 0) return part.Start;
            if (index <= part.Text.Length) return part.Plain ? part.Start.GetPositionAtOffset(index, LogicalDirection.Forward) ?? part.End : part.End;
            index -= part.Text.Length;
        }
        return Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
    }

    void FormatLinks()
    {
        if (_replacing) return;
        var text = Text;
        if (!text.Contains("http", StringComparison.OrdinalIgnoreCase)) return;
        var parts = Parts();
        var existing = new List<(int Start, int End)>();
        int position = 0;
        foreach (var part in parts)
        {
            if (part.Link) existing.Add((position, position + part.Text.Length));
            position += part.Text.Length;
        }
        int begin = OffsetOf(parts, Selection.Start), end = OffsetOf(parts, Selection.End);
        var matches = Urls().Matches(text).Reverse().ToArray();
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
                var start = new TextRange(PointAt(parts, match.Index), PointAt(parts, after)).Start;
                var chip = new InlineUIContainer(LinkChip(raw, uri), start) { Tag = raw, BaselineAlignment = BaselineAlignment.Center };
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

    static string TrimLink(string value)
    {
        static bool Unbalanced(string text, char close, char open) => text.EndsWith(close) && text.Count(c => c == close) > text.Count(c => c == open);
        var trimmed = value.TrimEnd('.', ',', ';', '!', ':', '»');
        while (Unbalanced(trimmed, ')', '(') || Unbalanced(trimmed, ']', '[')) trimmed = trimmed[..^1];
        return trimmed;
    }

    FrameworkElement LinkChip(string raw, Uri uri)
    {
        var title = new TextBlock
        {
            Text = raw.Length > 48 ? raw[..45] + "…" : raw, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 225, Margin = new(8, 0, 2, 0),
        };
        var remove = Theme.IconButton(Glyphs.Cancel, "Убрать ссылку: " + raw, 22, 10);
        var chip = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, remove } },
            CornerRadius = new(8), ToolTip = raw,
        }.Paint(Border.BackgroundProperty, "Surface3");
        AutomationProperties.SetName(chip, raw);
        remove.Click += (_, _) =>
        {
            if (FindLink(Document.Blocks.OfType<Paragraph>().SelectMany(block => block.Inlines), chip) is not { } inline) return;
            var caret = OffsetOf(Parts(), inline.ElementStart);
            new TextRange(inline.ElementStart, inline.ElementEnd).Text = "";
            Select(caret, 0);
            Focus();
        };
        _ = FillTitleAsync(uri, title);
        return chip;
    }

    static InlineUIContainer? FindLink(IEnumerable<Inline> inlines, FrameworkElement chip)
    {
        foreach (var inline in inlines)
        {
            if (inline is InlineUIContainer container && ReferenceEquals(container.Child, chip)) return container;
            if (inline is Span span && FindLink(span.Inlines, chip) is { } child) return child;
        }
        return null;
    }

    static async Task FillTitleAsync(Uri uri, TextBlock label)
    {
        if (await LinkTitles.GetAsync(uri) is { } title) label.Text = title;
    }
}
