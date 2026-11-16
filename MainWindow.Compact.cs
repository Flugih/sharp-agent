namespace SharpAgent;

public partial class MainWindow
{
    CompactChatWindow? _compactChat;
    FrameworkElement? _questionCard, _questionFocus;

    internal Window ActiveChatWindow => _compactChat is { IsVisible: true } compact ? compact : this;

    internal void ActivateChatWindow()
    {
        var window = ActiveChatWindow;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (window.Topmost) return;
        window.Topmost = true;
        window.Topmost = false;
    }

    void CompactMode_Click(object sender, RoutedEventArgs e)
    {
        if (_compactChat != null)
        {
            ActivateChatWindow();
            return;
        }
        if (VoiceShown)
        {
            ShowToast("Завершите голосовой ввод перед переключением окна.", ToastKind.Warning);
            return;
        }
        MoreMenu.IsOpen = false;
        CloseFlyout();
        CloseOverlay();
        SaveState();
        var compact = _compactChat = new CompactChatWindow(AnswerView) { Draft = InputBox.Text };
        compact.DraftChanged += text =>
        {
            var normalized = text.ReplaceLineEndings("\n");
            if (InputBox.Text != normalized) InputBox.Text = normalized;
        };
        compact.Send += () => _ = SendAsync();
        compact.Stop += StopAgent;
        compact.Closed += (_, _) =>
        {
            if (_compactChat != compact) return;
            compact.QuestionSlot.Content = null;
            _compactChat = null;
            if (Dispatcher.HasShutdownStarted) return;
            Show();
            ActivateChatWindow();
            PlaceQuestion();
            if (_questionCard == null) InputBox.Focus();
        };
        SyncCompactChat();
        UpdateCompactAction();
        if (_lastErrorCard?.Tag is string error) compact.ShowError(error);
        if (_cts != null)
        {
            compact.SetStatus(_questionCard == null ? "Размышление…" : "Жду ответа");
            if (!_draftIsProgress) compact.ShowDraft(_draftText.ToString());
        }
        else if (_pendingAttachments.Count > 0) compact.SetStatus($"Вложений к сообщению: {_pendingAttachments.Count}");
        compact.Show();
        Hide();
        ActivateChatWindow();
        PlaceQuestion();
        if (_questionCard == null) compact.FocusInput();
    }

    void SyncCompactChat() => _compactChat?.SyncHistory(_agent.History);

    void UpdateCompactAction()
    {
        if (_compactChat is not { } compact) return;
        compact.Draft = InputBox.Text;
        compact.UpdateAction(_cts != null, _cts is { } running ? !running.IsCancellationRequested : SendButton.IsEnabled,
            _agent.CanContinue && string.IsNullOrWhiteSpace(InputBox.Text) && _pendingAttachments.Count == 0);
    }

    void PlaceQuestion()
    {
        QuestionHost.Content = null;
        QuestionHost.Visibility = Visibility.Collapsed;
        var host = _compactChat?.QuestionSlot ?? QuestionHost;
        host.Content = _questionCard;
        host.Visibility = VisibleIf(_questionCard != null);
        if (_questionFocus is { } focus)
            _ = Dispatcher.InvokeAsync(() => { if (_questionFocus == focus) focus.Focus(); }, DispatcherPriority.Input);
    }
}
