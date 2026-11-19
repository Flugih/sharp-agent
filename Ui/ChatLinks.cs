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

    void Click(string path)
    {
        if (File.Exists(path)) openInApp(path);
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

    Inline WebLink(string url, string text)
    {
        var link = new Hyperlink(new Run(text)) { ToolTip = "Открыть во внешнем браузере\n" + url, Style = LinkStyle };
        link.Click += (_, _) => FileActions.OpenUrl(url);
        link.ContextMenu = new ContextMenu
        {
            Items =
            {
                FileActions.Item("Открыть во внешнем браузере", Glyphs.Globe, () => FileActions.OpenUrl(url)),
                FileActions.Item("Копировать ссылку", Glyphs.Copy, () => FileActions.Copy(url, "Ссылка скопирована")),
            },
        };
        return link;
    }

    public Button FileChip(string path, string? text = null, params MenuItem[] extra)
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
        AutomationProperties.SetName(chip, (Directory.Exists(path) ? "Папка " : "Файл ") + name);
        chip.Click += (_, _) => Click(path);
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
        AutomationProperties.SetName(link, (Directory.Exists(path) ? "Папка " : "Файл ") + name);
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
            FileActions.RaiseNotify("Буфер обмена занят другой программой — попробуйте ещё раз", true);
            return;
        }
        button.Content = Glyphs.Check;
        button.Paint(Control.ForegroundProperty, "Success");
        button.ToolTip = "Скопировано";
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
        var text = new TextBox
        {
            Text = code, IsReadOnly = true, IsReadOnlyCaretVisible = false, IsTabStop = false, FontFamily = Theme.Mono, FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new(0),
            Padding = new(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0),
        }.Paint(TextBox.ForegroundProperty, "TextSoft").Paint(TextBoxBase.SelectionBrushProperty, "Accent");
        TextBlock.SetLineHeight(text, 19);
        var label = language != null ? $"Скопировать код ({language})" : "Скопировать код";
        var copy = Theme.IconButton(Glyphs.Copy, label, 28, 14);
        copy.Click += (_, _) => ChatLinks.CopyWithFeedback(copy, code, label);
        var grid = new Grid { ColumnDefinitions = { new(), new() { Width = GridLength.Auto } }, Children = { text, copy } };
        Grid.SetColumn(copy, 1);
        copy.VerticalAlignment = VerticalAlignment.Center;
        text.SizeChanged += (_, _) => copy.VerticalAlignment = text.LineCount > 1 ? VerticalAlignment.Top : VerticalAlignment.Center;
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
