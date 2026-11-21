namespace SharpAgent;

public partial class MainWindow
{
    static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

    void ShowDiffPreview(string path)
    {
        var sections = new StackPanel();
        FrameworkElement? selected = null;
        if (_previewChanges is { } files)
        {
            for (int i = 0; i < files.Length; i++)
            {
                var file = files[i];
                var section = AddSection(file.Path, file.Undone ? null : file.Diff,
                    file.Undone ? Tr("Изменения отменены", "Changes undone") : Tr("Нет текстового сравнения", "No text diff available"));
                if (i == _previewSelected && i > 0) selected = section;
            }
        }
        else AddSection(path, _previewDiff, Tr("Нет текстового сравнения", "No text diff available"));
        var scroll = new ScrollViewer { Content = sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            for (var node = e.OriginalSource as DependencyObject; node != null && node != scroll; node = ParentOf(node))
                if (node is ScrollViewer inner && inner.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
                {
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 2d);
                    e.Handled = true;
                    break;
                }
        };
        PreviewBody.Content = scroll;
        if (selected != null) Dispatcher.InvokeAsync(() =>
        {
            if (PreviewBody.Content == scroll) selected.BringIntoView();
        }, DispatcherPriority.Loaded);

        FrameworkElement AddSection(string file, FileDiff? diff, string empty)
        {
            var header = new DockPanel { Margin = new(10, 0, 10, 0), Height = 32 };
            if (diff != null)
            {
                var counts = DiffSummary(diff.Added, diff.Removed);
                DockPanel.SetDock(counts, Dock.Right);
                header.Children.Add(counts);
            }
            header.Children.Add(GlyphText(CodeExtensions.Contains(Path.GetExtension(file)) ? Glyphs.Code : Glyphs.Document, 14, "TextMuted", new(0, 0, 8, 0)));
            header.Children.Add(new TextBlock
            {
                Text = CardTitle(file, keepExtension: true), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            }.Paint(ForegroundProperty, "TextSoft"));
            var section = new StackPanel
            {
                Children =
                {
                    new Border { Child = header, BorderThickness = new(0, 1, 0, 1) }.Paint(BorderBrushProperty, "Line"),
                    diff != null ? DiffView(file, diff, int.MaxValue) : DiffNote(empty),
                },
            };
            sections.Children.Add(section);
            return section;
        }
    }

    static FrameworkElement DiffView(string? path, FileDiff diff, int limit)
    {
        bool split = diff.Added > 0 && diff.Removed > 0, beforeOnly = diff.Added == 0;
        var left = new StackPanel();
        var right = new StackPanel();
        var extension = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
        List<(int Number, string Text)> removed = [], added = [];
        int oldLine = 1, newLine = 1, shown = 0;
        bool inHunk = false;
        foreach (var line in diff.Unified.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line == TextDiff.Truncated || shown >= limit)
            {
                FlushChanges();
                AddNote(Tr("… изменения показаны не полностью", "… diff truncated"));
                break;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushChanges();
                var header = HunkHeader.Match(line);
                bool hasNewLine = int.TryParse(header.Groups[2].Value, out var newStart);
                inHunk = int.TryParse(header.Groups[1].Value, out var oldStart) && hasNewLine;
                if (!inHunk) continue;
                int skipped = Math.Min(oldStart - oldLine, newStart - newLine);
                if (skipped > 0) AddNote(Tr($"Без изменений: {skipped}", $"{skipped} unchanged {(skipped == 1 ? "line" : "lines")}"));
                else if (oldStart < oldLine || newStart < newLine) AddNote(Tr("Следующий участок изменений", "Next changed section"));
                oldLine = oldStart;
                newLine = newStart;
                continue;
            }
            if (!inHunk || line[0] is not (' ' or '-' or '+')) continue;
            shown++;
            var code = line[1..];
            switch (line[0])
            {
                case '-': removed.Add((oldLine++, code)); break;
                case '+': added.Add((newLine++, code)); break;
                default:
                    FlushChanges();
                    if (split || beforeOnly) left.Children.Add(DiffLine(oldLine, code, extension));
                    if (split || !beforeOnly) right.Children.Add(DiffLine(newLine, code, extension));
                    oldLine++;
                    newLine++;
                    break;
            }
        }
        FlushChanges();
        if (!split) return Side(beforeOnly ? left : right);
        var pair = new Grid { ColumnDefinitions = { new(), new() { Width = new(1) }, new() } };
        var divider = new Border().Paint(BackgroundProperty, "LineStrong");
        var after = Side(right);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(after, 2);
        pair.Children.Add(Side(left));
        pair.Children.Add(divider);
        pair.Children.Add(after);
        return pair;

        void FlushChanges()
        {
            for (int i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                if (split || beforeOnly)
                    left.Children.Add(i < removed.Count ? DiffLine(removed[i].Number, removed[i].Text, extension, '-') : DiffGap());
                if (split || !beforeOnly)
                    right.Children.Add(i < added.Count ? DiffLine(added[i].Number, added[i].Text, extension, '+') : DiffGap());
            }
            removed.Clear();
            added.Clear();
        }

        void AddNote(string label)
        {
            if (split || beforeOnly) left.Children.Add(DiffNote(label));
            if (split || !beforeOnly) right.Children.Add(DiffNote(label));
        }
    }

    static ScrollViewer Side(StackPanel rows) => new()
    {
        Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        PanningMode = PanningMode.HorizontalOnly, Focusable = false,
    };

    static Border DiffNote(string label) => new Border
    {
        Padding = new(10, 5, 10, 5), Child = new TextBlock { Text = label, FontSize = 12 }.Paint(ForegroundProperty, "TextDim"),
    }.Paint(BackgroundProperty, "Fill");

    static Border DiffGap() => new Border { Height = 19 }.Paint(BackgroundProperty, "DiffGap");

    static Border DiffLine(int number, string code, string extension, char kind = ' ')
    {
        string? change = kind switch { '+' => "Success", '-' => "DangerText", _ => null };
        var text = new TextBlock { FontFamily = Theme.Mono, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }
            .Paint(ForegroundProperty, "TextSoft");
        DiffCode.Write(text, code, extension);
        var gutter = new TextBlock
        {
            Text = number.ToString(), Width = 40, Margin = new(0, 0, 8, 0), TextAlignment = TextAlignment.Right,
            FontFamily = Theme.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        }.Paint(ForegroundProperty, change ?? "TextMuted");
        var row = new Border
        {
            Height = 19, BorderThickness = new(2, 0, 0, 0), Padding = new(0, 0, 8, 0),
            Child = new DockPanel { Children = { gutter, text } },
        };
        return change == null ? row : row.Paint(BackgroundProperty, kind == '+' ? "DiffAdd" : "DiffRemove").Paint(BorderBrushProperty, change);
    }
}
