using System.Windows.Data;
using System.Windows.Documents;

namespace SharpAgent.Ui;

public sealed class ChatLinks(Window owner, PathRefs paths, Action<string> openInApp)
{
    public MarkdownContext Markdown => new(paths, FileLink, WebLink,
        (code, language) => new BlockUIContainer(new CodeBlockView(code, language)) { Margin = new(0, 4, 0, 12) });

    static string GlyphFor(string path) => Directory.Exists(path) ? Glyphs.Folder : Attachment.IsImagePath(path) ? Glyphs.Picture : Glyphs.Document;

    static Style? LinkStyle => Application.Current.TryFindResource(typeof(Hyperlink)) as Style;

    static string HintFor(string path) => Directory.Exists(path)
        ? $"{path}\nЩелчок — открыть папку, правая кнопка — другие действия"
        : $"{path}\nЩелчок — посмотреть здесь, правая кнопка — другие действия";

    void Click(string path, Action<string>? open = null)
    {
        if (File.Exists(path)) (open ?? openInApp)(path);
        else FileActions.Open(owner, path);
    }

    Inline FileLink(string path, string text)
    {
        var link = new Hyperlink
        {
            ToolTip = HintFor(path), Style = LinkStyle,
            Inlines = { new Run(GlyphFor(path) + "\u00A0") { FontFamily = Theme.Icons, FontSize = 13, BaselineAlignment = BaselineAlignment.Center }, new Run(text) },
        };
        link.Click += (_, _) => Click(path);
        link.ContextMenu = FileActions.BuildMenu(owner, path);
        FileActions.EnableDragOut(link, path);
        return link;
    }

    internal Inline WebLink(string url, string text)
    {
        var link = new Hyperlink(new Run(text)) { ToolTip = Tr("Открыть во внешнем браузере", "Open in external browser") + "\n" + url, Style = LinkStyle };
        link.Click += (_, _) => FileActions.OpenUrl(url);
        link.ContextMenu = new ContextMenu
        {
            Items =
            {
                FileActions.Item(Tr("Открыть во внешнем браузере", "Open in external browser"), Glyphs.Globe, () => FileActions.OpenUrl(url)),
                FileActions.Item(Tr("Копировать ссылку", "Copy link"), Glyphs.Copy, () => FileActions.Copy(url, Tr("Ссылка скопирована", "Link copied"))),
            },
        };
        return link;
    }

    public Button FileChip(string path, string? text = null, params MenuItem[] extra) => FileChip(path, text, null, extra);

    internal Button FileChip(string path, string? text, Action<string>? open, MenuItem[] extra)
    {
        var name = text ?? DisplayName(path);
        var chip = new Button
        {
            Style = Theme.Style("FileChip"),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = GlyphFor(path), FontFamily = Theme.Icons, FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 1, 6, 0),
                    }.Paint(TextBlock.ForegroundProperty, "TextDim"),
                    new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
            ToolTip = HintFor(path),
            ContextMenu = FileActions.BuildMenu(owner, path, extra),
        };
        AutomationProperties.SetName(chip, (Directory.Exists(path) ? Tr("Папка ", "Folder ") : Tr("Файл ", "File ")) + name);
        chip.Click += (_, _) => Click(path, open);
        FileActions.EnableDragOut(chip, path);
        return chip;
    }

    // A file name inside a quiet row: plain text with a dotted underline, the same actions as a chip.
    public TextBlock FileRef(string path, string? text = null)
    {
        var name = text ?? DisplayName(path);
        var pen = new Pen { Thickness = 1, DashStyle = DashStyles.Dot };
        var link = new Hyperlink(new Run(name))
        {
            ToolTip = HintFor(path), Style = LinkStyle, ContextMenu = FileActions.BuildMenu(owner, path),
            TextDecorations = [new TextDecoration { Location = TextDecorationLocation.Underline, Pen = pen }],
        }.Paint(TextElement.ForegroundProperty, "TextSoft");
        BindingOperations.SetBinding(pen, Pen.BrushProperty, new Binding(nameof(link.Foreground)) { Source = link });
        AutomationProperties.SetName(link, (Directory.Exists(path) ? Tr("Папка ", "Folder ") : Tr("Файл ", "File ")) + name);
        link.Click += (_, _) => Click(path);
        FileActions.EnableDragOut(link, path);
        return new TextBlock(link) { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    }

    public static string DisplayName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    public static void CopyWithFeedback(Button button, string text, string normalHint)
    {
        if (!TextClipboard.SetText(text))
        {
            FileActions.RaiseNotify(Tr("Буфер обмена занят другой программой — попробуйте ещё раз", "The clipboard is busy. Try again."), true);
            return;
        }
        button.Content = Glyphs.Check;
        button.Paint(Control.ForegroundProperty, "Success");
        button.ToolTip = Tr("Скопировано", "Copied");
        Theme.After(TimeSpan.FromSeconds(1.4), () =>
        {
            button.Content = Glyphs.Copy;
            button.ClearValue(Control.ForegroundProperty);
            button.ToolTip = normalHint;
        });
    }
}

sealed class CodeBlockView : Border
{
    public CodeBlockView(string code, string? language)
    {
        var paragraph = new Paragraph { Margin = new(0) };
        CodeSyntax.WriteBlock(paragraph.Inlines, code, language);
        var document = new FlowDocument(paragraph)
        {
            PagePadding = new(0), FontFamily = Theme.Mono, FontSize = 12.5 + ChatTextSize.Extra,
            LineHeight = 19, TextAlignment = TextAlignment.Left,
        }.Paint(TextElement.ForegroundProperty, "TextSoft");
        var text = new RichTextBox(document)
        {
            IsReadOnly = true, IsReadOnlyCaretVisible = false, IsTabStop = false, AutoWordSelection = false,
            Background = Brushes.Transparent, BorderThickness = new(0), FocusVisualStyle = null,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center, Margin = new(-5, 0, 3, 0),
        }.Paint(TextBoxBase.SelectionBrushProperty, "Accent");
        var label = Tr("Скопировать код", "Copy code") + (language != null ? $" ({language})" : "");
        var copy = Theme.IconButton(Glyphs.Copy, label, 28, 14);
        copy.HorizontalContentAlignment = HorizontalAlignment.Center;
        TextBlock.SetLineHeight(copy, double.NaN);
        TextBlock.SetLineStackingStrategy(copy, LineStackingStrategy.MaxHeight);
        copy.Click += (_, _) => ChatLinks.CopyWithFeedback(copy, code, label);
        var grid = new Grid { ColumnDefinitions = { new(), new() { Width = GridLength.Auto } }, Children = { text, copy } };
        Grid.SetColumn(copy, 1);
        copy.VerticalAlignment = VerticalAlignment.Center;
        SetResourceReference(BackgroundProperty, "BlockBg");
        CornerRadius = new(12);
        Padding = new(12, 8, 8, 8);
        Child = grid;
        RequestBringIntoView += (_, e) =>
        {
            if (IsMouseCaptureWithin || IsMouseOver && InputManager.Current.MostRecentInputDevice is not KeyboardDevice ||
                e.TargetObject is Visual target && (ReferenceEquals(target, text) || text.IsAncestorOf(target)))
                e.Handled = true;
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, this) || ReferenceEquals(e.OriginalSource, grid)) e.Handled = true;
        };
    }
}
