using System.Windows.Media.Effects;

namespace SharpAgent.Ui;

public sealed class DarkDialog : Window
{
    DarkDialog(Window owner, string title, FrameworkElement body, FrameworkElement buttons)
    {
        Owner = owner;
        Title = title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 560;
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
        this.Paint(ForegroundProperty, "Text");
        AutomationProperties.SetName(this, title);
        var header = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        // The shadow sits on its own layer so the text is not drawn through the effect and stays sharp.
        Content = new Grid
        {
            Margin = new(20),
            Children =
            {
                new Border { CornerRadius = new(16), Effect = (Effect)Application.Current.Resources["PopupShadow"] }.Paint(Border.BackgroundProperty, "Surface2"),
                new Border
                {
                    BorderThickness = new(1), CornerRadius = new(16), Padding = new(24),
                    Child = new DockPanel
                    {
                        Children = { header, buttons, new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false } },
                    },
                }.Paint(Border.BorderBrushProperty, "RaisedLine"),
            },
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    public static bool Ask(Window owner, string title, string message, string okText,
        string? cancelText = null, bool danger = false, string? code = null, bool delayed = false, CancellationToken ct = default)
    {
        if (code != null)
        {
            code = string.Concat(code.ReplaceLineEndings("\n").Select(ch => ch is '\n' or '\t' ||
                char.GetUnicodeCategory(ch) is not (UnicodeCategory.Format or UnicodeCategory.Control) ? ch.ToString() : $"⟨U+{(int)ch:X4}⟩"));
            int lines = code.Count(ch => ch == '\n') + 1;
            if (lines > 12 || code.Length > 700) message += (message.Length > 0 ? "\n" : "") +
                Tr($"Строк в тексте: {lines} — прокрутите его до конца.", $"There are {lines} lines. Scroll to the end.");
        }
        var body = new StackPanel();
        if (message.Length > 0) body.Children.Add(ReadOnlyText(message, "TextSoft", Theme.Ui, 14, 340));
        if (code != null)
            body.Children.Add(new Border
            {
                CornerRadius = new(8), Padding = new(12, 10, 12, 10), Margin = new(0, message.Length > 0 ? 12 : 0, 0, 0),
                Child = ReadOnlyText(code, "Text", Theme.Mono, 12.5, 220),
            }.Paint(Border.BackgroundProperty, "BlockBg"));
        var ok = new Button { Content = okText, IsDefault = !danger && !delayed, IsEnabled = !delayed, Margin = new(8, 0, 0, 0), Style = Theme.Style(danger ? "DangerButton" : "PrimaryButton") };
        var cancel = new Button { Content = cancelText ?? Tr("Отмена", "Cancel"), IsCancel = true };
        var dialog = new DarkDialog(owner, title, body, new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 24, 0, 0), Children = { cancel, ok },
        });
        ok.Click += (_, _) => dialog.DialogResult = true;
        cancel.Click += (_, _) => dialog.DialogResult = false;
        dialog.Loaded += (_, _) =>
        {
            (danger || delayed ? cancel : ok).Focus();
            if (delayed) Theme.After(TimeSpan.FromMilliseconds(400), () => ok.IsEnabled = true);
        };
        using var cancellation = ct.Register(() => _ = dialog.Dispatcher.InvokeAsync(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        }));
        return dialog.Run() == true;
    }

    public static void Show(Window owner, string title, FrameworkElement content, string? closeText = null)
    {
        var close = new Button { Content = closeText ?? Tr("Закрыть", "Close"), IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 24, 0, 0) };
        var dialog = new DarkDialog(owner, title, content, close);
        close.Click += (_, _) => dialog.Close();
        dialog.Loaded += (_, _) => close.Focus();
        dialog.Run();
    }

    // Dims the main window behind the dialog; a nested dialog leaves the dim to the one still open.
    bool? Run()
    {
        if (Owner is not MainWindow main) return ShowDialog();
        main.DialogDim.Visibility = Visibility.Visible;
        try { return ShowDialog(); }
        finally
        {
            if (!main.OwnedWindows.OfType<DarkDialog>().Any(other => other != this && other.IsVisible)) main.DialogDim.Visibility = Visibility.Collapsed;
        }
    }

    static TextBox ReadOnlyText(string text, string foreground, FontFamily font, double size, double maxHeight) => new TextBox
    {
        Text = text, IsReadOnly = true, IsTabStop = false, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent,
        BorderThickness = new(0), Padding = new(0), FontFamily = font, FontSize = size, MaxHeight = maxHeight,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ContextMenu = Application.Current.TryFindResource("ReadOnlyTextMenu") as ContextMenu,
    }.Paint(ForegroundProperty, foreground).Paint(TextBoxBase.SelectionBrushProperty, "Accent");
}
