using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SharpAgent.Agent;

namespace SharpAgent.Ui;

public sealed class ChatLinks(Window owner, PathRefs paths, Action<string> openInApp)
{
    public MarkdownContext Markdown => new()
    {
        Paths = paths,
        FileLink = FileLink,
        WebLink = WebLink,
        CodeBlock = CodeBlock,
    };

    public static string GlyphFor(string path) =>
        Directory.Exists(path) ? Glyphs.Folder
        : Attachment.IsImagePath(path) ? Glyphs.Picture
        : Glyphs.Document;

    private static Style? LinkStyle => Application.Current.TryFindResource(typeof(Hyperlink)) as Style;

    private static string HintFor(string path) => Directory.Exists(path)
        ? $"{path}\nЩелчок — открыть папку, правая кнопка — другие действия"
        : $"{path}\nЩелчок — посмотреть здесь, правая кнопка — другие действия";

    private void Click(string path)
    {
        if (File.Exists(path)) openInApp(path);
        else FileActions.Open(owner, path);
    }

    private Inline FileLink(string path, string text)
    {
        var link = new Hyperlink { ToolTip = HintFor(path), Style = LinkStyle };
        link.Inlines.Add(new Run(GlyphFor(path) + " ")
        {
            FontFamily = Theme.Icons,
            FontSize = 13,
            BaselineAlignment = BaselineAlignment.Center,
        });
        link.Inlines.Add(new Run(text));
        link.Click += (_, _) => Click(path);
        link.ContextMenu = FileActions.BuildMenu(owner, path);
        FileActions.EnableDragOut(link, path);
        return link;
    }

    private Inline WebLink(string url, string text)
    {
        var link = new Hyperlink(new Run(text)) { ToolTip = url, Style = LinkStyle, TextDecorations = TextDecorations.Underline };
        link.Click += (_, _) => FileActions.OpenUrl(url);

        var menu = new ContextMenu();
        menu.Items.Add(FileActions.Item("Открыть ссылку", Glyphs.Globe, () => FileActions.OpenUrl(url)));
        menu.Items.Add(FileActions.Item("Копировать ссылку", Glyphs.Copy, () => FileActions.Copy(url, "Ссылка скопирована")));
        link.ContextMenu = menu;
        return link;
    }

    public Button FileChip(string path, string? text = null, params MenuItem[] extra)
    {
        var name = text ?? DisplayName(path);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = GlyphFor(path), FontFamily = Theme.Icons, FontSize = 13, Foreground = Theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = name, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var chip = new Button
        {
            Style = Theme.Style("FileChip"),
            Content = content,
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
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private Block CodeBlock(string code, string? language)
    {
        var text = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsTabStop = false,
            FontFamily = Theme.Mono,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.TextSoft,
            SelectionBrush = Theme.Accent,
            Padding = new Thickness(0, 7, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var label = language != null ? $"Скопировать код ({language})" : "Скопировать код";
        var copy = Theme.IconButton(Glyphs.Copy, label, 24);
        copy.HorizontalContentAlignment = HorizontalAlignment.Center;
        copy.Padding = new Thickness(0, 8, 0, 0);
        copy.Click += (_, _) => CopyWithFeedback(copy, code, label);

        return new BlockUIContainer(new CodeBlockView(text, copy))
        {
            Margin = new Thickness(0, 5, 0, 12),
        };
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
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = Glyphs.Copy;
            button.ClearValue(Control.ForegroundProperty);
            button.ToolTip = normalHint;
        };
        timer.Start();
    }
}

internal sealed class CodeBlockView : Border
{
    public CodeBlockView(TextBox text, Button copy)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        grid.Children.Add(copy);
        Grid.SetColumn(copy, 1);
        copy.VerticalAlignment = VerticalAlignment.Center;
        text.SizeChanged += (_, _) =>
            copy.VerticalAlignment = text.LineCount > 1 ? VerticalAlignment.Top : VerticalAlignment.Center;

        Background = Theme.Surface;
        BorderBrush = Theme.Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12, 6, 6, 6);
        Child = grid;

        RequestBringIntoView += (_, e) =>
        {
            if (IsMouseCaptureWithin ||
                IsMouseOver && InputManager.Current.MostRecentInputDevice is not KeyboardDevice ||
                e.TargetObject is Visual target && (ReferenceEquals(target, text) || text.IsAncestorOf(target)))
                e.Handled = true;
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, this) || ReferenceEquals(e.OriginalSource, grid))
                e.Handled = true;
        };
    }
}
