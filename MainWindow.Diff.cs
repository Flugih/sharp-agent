using System.Windows.Documents;
using Glyphs = SharpAgent.Ui.Glyphs;

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

    internal static FrameworkElement DiffView(string? path, FileDiff diff, int limit)
    {
        bool split = diff.Added > 0 && diff.Removed > 0, beforeOnly = diff.Added == 0;
        var extension = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
        var left = new DiffSide(extension);
        var right = new DiffSide(extension);
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
                    if (split || beforeOnly) left.Add(oldLine, code);
                    if (split || !beforeOnly) right.Add(newLine, code);
                    oldLine++;
                    newLine++;
                    break;
            }
        }
        FlushChanges();
        if (!split) return (beforeOnly ? left : right).View();
        var pair = new Grid { ColumnDefinitions = { new(), new() { Width = new(1) }, new() } };
        var divider = new Border().Paint(BackgroundProperty, "LineStrong");
        double pageWidth = Math.Max(left.PageWidth, right.PageWidth);
        var before = left.View(pageWidth);
        var after = right.View(pageWidth);
        double? pendingBefore = null, pendingAfter = null;
        before.ScrollChanged += (_, e) => SyncHorizontal(before, after, e, ref pendingBefore, ref pendingAfter);
        after.ScrollChanged += (_, e) => SyncHorizontal(after, before, e, ref pendingAfter, ref pendingBefore);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(after, 2);
        pair.Children.Add(before);
        pair.Children.Add(divider);
        pair.Children.Add(after);
        return pair;

        static void SyncHorizontal(ScrollViewer source, ScrollViewer target, ScrollChangedEventArgs e,
            ref double? pendingSource, ref double? pendingTarget)
        {
            if (!ReferenceEquals(e.OriginalSource, source) || e.HorizontalChange == 0) return;
            bool mirrored = pendingSource == e.HorizontalOffset;
            pendingSource = null;
            if (mirrored) return;
            double offset = Math.Min(e.HorizontalOffset, target.ScrollableWidth);
            if (offset == (pendingTarget ?? target.HorizontalOffset)) return;
            pendingTarget = offset;
            target.ScrollToHorizontalOffset(offset);
        }

        void FlushChanges()
        {
            for (int i = 0; i < Math.Max(removed.Count, added.Count); i++)
            {
                if (split || beforeOnly)
                {
                    if (i < removed.Count) left.Add(removed[i].Number, removed[i].Text, '-');
                    else left.Add(null, "");
                }
                if (split || !beforeOnly)
                {
                    if (i < added.Count) right.Add(added[i].Number, added[i].Text, '+');
                    else right.Add(null, "");
                }
            }
            removed.Clear();
            added.Clear();
        }

        void AddNote(string label)
        {
            if (split || beforeOnly) left.Add(null, label, '@');
            if (split || !beforeOnly) right.Add(null, label, '@');
        }
    }

    static Border DiffNote(string label) => new Border
    {
        Padding = new(10, 5, 10, 5), Child = new TextBlock { Text = label, FontSize = 12 }.Paint(ForegroundProperty, "TextDim"),
    }.Paint(BackgroundProperty, "Fill");

    sealed class DiffSide(string extension)
    {
        const int RowHeight = 19;
        readonly StackPanel _gutter = new() { Width = 50, IsHitTestVisible = false };
        readonly FlowDocument _document = new()
        {
            FontFamily = Theme.Mono, FontSize = 12.5, PagePadding = new(0), LineHeight = RowHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        int _columns = 8;
        public double PageWidth => Math.Clamp(_columns * 8d + 8, 160, 32000);

        public void Add(int? number, string code, char kind = ' ')
        {
            string? change = kind switch { '+' => "Success", '-' => "DangerText", _ => null };
            string? background = kind switch { '+' => "DiffAdd", '-' => "DiffRemove", '@' => "Fill", _ when number == null => "DiffGap", _ => null };
            var paragraph = new Paragraph { Margin = new(0) }.Paint(TextElement.ForegroundProperty, kind == '@' ? "TextDim" : "TextSoft");
            if (kind == '@')
            {
                paragraph.FontFamily = Theme.Ui;
                paragraph.FontSize = 12;
                paragraph.Inlines.Add(new Run(code));
            }
            else if (number.HasValue) CodeSyntax.Write(paragraph.Inlines, code, extension);
            else paragraph.Inlines.Add(new Run(""));
            _columns = Math.Max(_columns, code.Length + 3 * code.Count(character => character == '\t'));
            var gutter = new Border
            {
                Height = RowHeight, BorderThickness = new(2, 0, 0, 0), Padding = new(0, 0, 8, 0),
                Child = new TextBlock { Text = number?.ToString() ?? "", TextAlignment = TextAlignment.Right,
                    FontFamily = Theme.Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Paint(ForegroundProperty, change ?? "TextMuted"),
            };
            if (background != null)
            {
                paragraph.Paint(TextElement.BackgroundProperty, background);
                gutter.Paint(BackgroundProperty, background);
            }
            if (change != null) gutter.Paint(BorderBrushProperty, change);
            _document.Blocks.Add(paragraph);
            _gutter.Children.Add(gutter);
        }

        public ScrollViewer View(double? pageWidth = null)
        {
            double page = pageWidth ?? PageWidth;
            _document.PageWidth = page;
            var box = ReadOnlyChatText(_document, Tr("Код изменений", "Changed code"));
            box.Margin = new(0);
            box.Width = page + 10;
            box.Height = Math.Max(RowHeight, _gutter.Children.Count * RowHeight);
            var content = new DockPanel { Children = { _gutter, box } };
            DockPanel.SetDock(_gutter, Dock.Left);
            return new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.HorizontalOnly, Focusable = false };
        }
    }
}
