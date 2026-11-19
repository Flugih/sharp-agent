using System.Windows.Interop;

namespace SharpAgent.Ui;

public partial class CompactChatWindow : Window
{
    readonly Func<string, DateTimeOffset?, StackPanel> _answerView;
    JsonObject[] _shown = [];
    double? _anchorBottom;

    internal event Action<string>? DraftChanged;
    internal event Action? Send, Stop;

    internal CompactChatWindow(Func<string, DateTimeOffset?, StackPanel> answerView)
    {
        _answerView = answerView;
        InitializeComponent();
    }

    internal string Draft
    {
        get => PromptInput.Text;
        set
        {
            if (PromptInput.Text.ReplaceLineEndings("\n") == value) return;
            PromptInput.Text = value;
            PromptInput.CaretIndex = value.Length;
        }
    }

    internal void FocusInput() => PromptInput.Focus();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(DisplayHook);
    }

    nint DisplayHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Display, taskbar or DPI changes can leave the window off its monitor; re-fit once WPF has applied them.
        if (message is 0x007E or 0x001A or 0x02E0) Dispatcher.InvokeAsync(Anchor, DispatcherPriority.Background);
        return 0;
    }

    internal void SetStatus(string text, string color = "TextMuted")
    {
        StatusText.Text = text;
        StatusText.Base = color;
        ShimmerStatus();
        StatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        UpdateTranscript(scroll: true);
    }

    internal void UpdateAction(bool running, bool enabled, bool resume)
    {
        ActionButton.Content = running ? "\uE71A" : resume ? Glyphs.Play : Glyphs.Send;
        var label = running ? "Остановить агента" : resume ? "Продолжить задачу" : "Отправить";
        ActionButton.ToolTip = !running && resume ? label : null;
        AutomationProperties.SetName(ActionButton, label);
        ActionButton.IsEnabled = enabled;
        ShimmerStatus();
        UpdateTranscript(scroll: running);
    }

    // Only a running agent's neutral status shimmers; results and warnings keep their colour.
    void ShimmerStatus() => StatusText.IsActive = StatusText.Base == "TextMuted" && Equals(ActionButton.Content, "");

    internal void SyncHistory(JsonObject[] messages)
    {
        if (_shown.SequenceEqual(messages)) return;
        _shown = messages;
        Transcript.Children.Clear();
        foreach (var message in messages)
            if (AgentRunner.Role(message) == "assistant")
                Transcript.Children.Add(_answerView(AgentRunner.ExtractText(message["content"])!.Trim(), AgentRunner.SentAtOf(message)));
            else Transcript.Children.Add(new Border
            {
                CornerRadius = new(14), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(24, 6, 2, 4), Padding = new(12, 6, 12, 6),
                Child = new TextBlock { Text = UserText(message), TextWrapping = TextWrapping.Wrap }.Paint(ForegroundProperty, "UserBubbleText"),
            }.Paint(BackgroundProperty, "UserBubble"));
        UpdateTranscript(scroll: true);
    }

    static string UserText(JsonObject message)
    {
        var text = AgentRunner.ExtractText(message["content"]) ?? "";
        var files = Attachment.ListFrom(message);
        return files.Count == 0 ? text : (text.Length == 0 ? "" : text + "\n") + string.Join(", ", files.Select(file => file.Name));
    }

    internal void ShowDraft(string? text)
    {
        StreamText.Text = text ?? "";
        StreamText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        UpdateTranscript(scroll: !string.IsNullOrEmpty(text));
    }

    internal void ShowError(string? text)
    {
        ErrorText.Text = text ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        UpdateTranscript(scroll: !string.IsNullOrEmpty(text));
    }

    internal void ShowChanges(FrameworkElement? content)
    {
        if (content == null && ChangesPill.Content == null) return;
        ChangesPill.Content = content;
        ChangesPill.Visibility = content != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateTranscript(scroll: content != null);
    }

    void UpdateTranscript(bool scroll)
    {
        TranscriptCard.Visibility = Transcript.Children.Count > 0 || new UIElement[] { StatusText, StreamText, ErrorText, ChangesPill }
            .Any(item => item.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        // Layout has not run yet, so the offsets still say whether the reader was at the bottom before this change.
        if (scroll && TranscriptScroll.VerticalOffset >= TranscriptScroll.ScrollableHeight - 1) TranscriptScroll.ScrollToEnd();
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var work = WorkArea();
        Left = work.Right - ActualWidth - 12;
        _anchorBottom = work.Bottom - 13;
        Anchor();
        FocusInput();
    }

    void Window_SizeChanged(object sender, SizeChangedEventArgs e) => Anchor();

    void Anchor()
    {
        if (_anchorBottom is not { } bottom) return;
        var work = WorkArea();
        Left = Fit(Left, ActualWidth, work.Left, work.Right);
        Top = Fit(bottom - ActualHeight, ActualHeight, work.Top, work.Bottom);
    }

    static double Fit(double start, double size, double min, double max) => Math.Max(min, Math.Min(start, max - size));

    Rect WorkArea()
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return PresentationSource.FromVisual(this) is { CompositionTarget: { } target } &&
            GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(this).Handle, 2), ref info)
            ? Rect.Transform(new(info.Left, info.Top, info.Right - info.Left, info.Bottom - info.Top), target.TransformFromDevice)
            : SystemParameters.WorkArea;
    }

    void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        _anchorBottom = Top + ActualHeight;
        Anchor();
    }

    void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (Equals(ActionButton.Content, "\uE71A")) Stop?.Invoke();
        else Send?.Invoke();
        if (QuestionSlot.Visibility != Visibility.Visible) FocusInput();
    }

    void FullWindow_Click(object sender, RoutedEventArgs e) => Close();

    void PromptInput_TextChanged(object sender, TextChangedEventArgs e) => DraftChanged?.Invoke(Draft);

    void PromptInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (!Equals(ActionButton.Content, "\uE71A") && ActionButton.IsEnabled) Send?.Invoke();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !Equals(ActionButton.Content, "\uE71A")) return;
        e.Handled = true;
        Stop?.Invoke();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo { public int Size, MonitorLeft, MonitorTop, MonitorRight, MonitorBottom, Left, Top, Right, Bottom, Flags; }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
