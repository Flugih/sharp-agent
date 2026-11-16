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

    internal void SetStatus(string text) => StatusText.Text = text;

    internal void UpdateAction(bool running, bool enabled, bool resume)
    {
        ActionButton.Content = running ? "\uE71A" : resume ? Glyphs.Play : Glyphs.Send;
        var label = running ? "Остановить агента" : resume ? "Продолжить задачу" : "Отправить";
        ActionButton.ToolTip = label;
        AutomationProperties.SetName(ActionButton, label);
        ActionButton.IsEnabled = enabled;
    }

    internal void SyncHistory(IReadOnlyList<JsonNode> history)
    {
        var messages = history.OfType<JsonObject>()
            .Where(m => AgentRunner.Role(m) == "user" || AgentRunner.Role(m) == "assistant" &&
                m["tool_calls"] == null && !string.IsNullOrWhiteSpace(AgentRunner.ExtractText(m["content"])))
            .TakeLast(16).ToArray();
        if (_shown.SequenceEqual(messages)) return;
        _shown = messages;
        var heading = messages.LastOrDefault(m => AgentRunner.Role(m) == "user");
        PromptTitle.Text = heading == null ? "Sharp Agent" : UserText(heading).ReplaceLineEndings(" ");
        Transcript.Children.Clear();
        foreach (var message in messages)
            if (AgentRunner.Role(message) == "assistant")
                Transcript.Children.Add(_answerView(AgentRunner.ExtractText(message["content"])!.Trim(), AgentRunner.SentAtOf(message)));
            else if (message != heading) Transcript.Children.Add(new Border
            {
                Background = Theme.Surface3, CornerRadius = new(10), HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new(18, 6, 2, 3), Padding = new(9, 5, 9, 5),
                Child = new TextBlock { Text = UserText(message), Foreground = Theme.TextSoft, TextWrapping = TextWrapping.Wrap },
            });
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

    void UpdateTranscript(bool scroll)
    {
        TranscriptCard.Visibility = Transcript.Children.Count > 0 || StreamText.Visibility == Visibility.Visible ||
            ErrorText.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (scroll) TranscriptScroll.ScrollToEnd();
    }

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left, work.Right - ActualWidth - 12);
        _anchorBottom = work.Bottom - 13;
        Anchor();
        FocusInput();
    }

    void Window_SizeChanged(object sender, SizeChangedEventArgs e) => Anchor();

    void Anchor()
    {
        if (_anchorBottom is { } bottom) Top = Math.Max(SystemParameters.VirtualScreenTop, bottom - ActualHeight);
    }

    void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        _anchorBottom = Top + ActualHeight;
    }

    void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (Equals(ActionButton.Content, "\uE71A")) Stop?.Invoke();
        else Send?.Invoke();
        if (QuestionSlot.Visibility != Visibility.Visible) FocusInput();
    }

    void FullWindow_Click(object sender, RoutedEventArgs e) => Close();

    void PromptInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PromptHint != null) PromptHint.Visibility = Draft.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DraftChanged?.Invoke(Draft);
    }

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
}
