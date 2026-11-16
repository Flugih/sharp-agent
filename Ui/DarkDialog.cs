using System.Windows.Media.Effects;

namespace SharpAgent.Ui;

public sealed class DarkDialog : Window
{
    DarkDialog(Window owner, string title, bool danger, FrameworkElement body, FrameworkElement buttons, double width)
    {
        Owner = owner;
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = width;
        if (owner is CompactChatWindow)
        {
            MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 32);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
        }
        else
        {
            if (owner.WindowState != WindowState.Minimized) MaxHeight = owner.ActualHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        FontFamily = Theme.Ui;
        FontSize = 14;
        Foreground = Theme.Text;
        AutomationProperties.SetName(this, title);
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 12),
            Children =
            {
                new TextBlock
                {
                    Text = danger ? Glyphs.Warning : Glyphs.Info, FontFamily = Theme.Icons, FontSize = 16,
                    Foreground = danger ? Theme.DangerText : Theme.AccentText, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 1, 12, 0),
                },
                new TextBlock
                {
                    Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center, MaxWidth = width - 110,
                },
            },
        };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Content = new Border
        {
            Background = Theme.Surface2, BorderBrush = Theme.LineStrong, BorderThickness = new(1), CornerRadius = new(12), Padding = new(24, 20, 24, 20),
            Margin = new(20), Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
            Child = new DockPanel
            {
                Children = { header, buttons, new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false } },
            },
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    public static bool Ask(Window owner, string title, string message, string okText,
        string cancelText = "Отмена", bool danger = false, string? code = null, bool delayed = false)
    {
        if (code != null)
        {
            code = string.Concat(code.ReplaceLineEndings("\n").Select(ch => ch is '\n' or '\t' ||
                char.GetUnicodeCategory(ch) is not (UnicodeCategory.Format or UnicodeCategory.Control) ? ch.ToString() : $"⟨U+{(int)ch:X4}⟩"));
            int lines = code.Count(ch => ch == '\n') + 1;
            if (lines > 12 || code.Length > 700) message += (message.Length > 0 ? "\n" : "") + $"Строк в тексте: {lines} — прокрутите его до конца.";
        }
        var body = new StackPanel();
        if (message.Length > 0) body.Children.Add(ReadOnlyText(message, Theme.TextSoft, Theme.Ui, 14, 340));
        if (code != null)
            body.Children.Add(new Border
            {
                Background = Theme.Bg, BorderBrush = Theme.LineStrong, BorderThickness = new(1), CornerRadius = new(8), Padding = new(10, 8, 10, 8),
                Margin = new(0, message.Length > 0 ? 12 : 0, 0, 0), Child = ReadOnlyText(code, Theme.Text, Theme.Mono, 13, 220),
            });
        var ok = new Button
        {
            Content = okText, MinWidth = 112, Height = 32, IsDefault = !danger && !delayed, IsEnabled = !delayed,
            Style = Theme.Style(danger ? "DangerPillButton" : "AccentPillButton"),
        };
        var cancel = new Button { Content = cancelText, MinWidth = 112, Height = 32, IsCancel = true, Margin = new(8, 0, 0, 0) };
        var dialog = new DarkDialog(owner, title, danger, body, new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0), Children = { ok, cancel },
        }, 520);
        ok.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        dialog.Loaded += (_, _) =>
        {
            (danger || delayed ? cancel : ok).Focus();
            if (delayed) Theme.After(TimeSpan.FromMilliseconds(400), () => ok.IsEnabled = true);
        };
        return dialog.ShowDialog() == true;
    }

    public static void Show(Window owner, string title, FrameworkElement content, double width = 520, string closeText = "Закрыть")
    {
        var close = new Button
        {
            Content = closeText, MinWidth = 112, Height = 32, IsCancel = true, IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0),
        };
        var dialog = new DarkDialog(owner, title, false, content, close, width);
        close.Click += (_, _) => dialog.Close();
        dialog.Loaded += (_, _) => close.Focus();
        dialog.ShowDialog();
    }

    static TextBox ReadOnlyText(string text, Brush foreground, FontFamily font, double size, double maxHeight) => new()
    {
        Text = text, IsReadOnly = true, IsTabStop = false, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent,
        BorderThickness = new(0), Padding = new(0), Foreground = foreground, SelectionBrush = Theme.Accent, FontFamily = font, FontSize = size,
        MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        ContextMenu = Application.Current.TryFindResource("ReadOnlyTextMenu") as ContextMenu,
    };
}
