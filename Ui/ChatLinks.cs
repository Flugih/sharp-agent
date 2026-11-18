using System.Windows.Documents;

namespace SharpAgent.Ui;

public sealed class ChatLinks(Window owner, PathRefs paths, Action<string> openInApp)
{
    public MarkdownContext Markdown => new(paths, FileLink, WebLink,
        (code, language) => new BlockUIContainer(new CodeBlockView(code, language)) { Margin = new(0, 5, 0, 12) });

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
        var link = new Hyperlink(new Run(text)) { ToolTip = "Открыть во внешнем браузере\n" + url, Style = LinkStyle, TextDecorations = TextDecorations.Underline };
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
                        Text = GlyphFor(path), FontFamily = Theme.Icons, FontSize = 13, Foreground = Theme.TextMuted,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 1, 6, 0),
                    },
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
        button.Foreground = Theme.Success;
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
            Text = code, IsReadOnly = true, IsReadOnlyCaretVisible = false, IsTabStop = false, FontFamily = Theme.Mono, FontSize = 13,
            TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new(0), Foreground = Theme.TextSoft,
            SelectionBrush = Theme.Accent, Padding = new(0, 7, 0, 0), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0),
        };
        var label = language != null ? $"Скопировать код ({language})" : "Скопировать код";
        var copy = Theme.IconButton(Glyphs.Copy, label, 24);
        copy.HorizontalContentAlignment = HorizontalAlignment.Center;
        copy.Padding = new(0, 8, 0, 0);
        copy.Click += (_, _) => ChatLinks.CopyWithFeedback(copy, code, label);
        var grid = new Grid { ColumnDefinitions = { new(), new() { Width = GridLength.Auto } }, Children = { text, copy } };
        Grid.SetColumn(copy, 1);
        copy.VerticalAlignment = VerticalAlignment.Center;
        text.SizeChanged += (_, _) => copy.VerticalAlignment = text.LineCount > 1 ? VerticalAlignment.Top : VerticalAlignment.Center;
        Background = Theme.Surface;
        BorderBrush = Theme.Line;
        BorderThickness = new(1);
        CornerRadius = new(8);
        Padding = new(12, 6, 6, 6);
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
