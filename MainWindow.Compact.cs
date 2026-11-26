using System.Windows.Interop;

namespace SharpAgent;

public partial class MainWindow
{
    CompactChatWindow? _compactChat;

    string ComposerDraft
    {
        get => _compactChat?.Draft ?? InputBox.Text;
        set
        {
            if (_compactChat is { } compact) compact.Draft = value;
            else
            {
                InputBox.Text = value;
                InputBox.CaretIndex = InputBox.Text.Length;
            }
        }
    }

    internal Window ActiveChatWindow => _compactChat is { IsVisible: true } compact ? compact : this;

    internal void ActivateChatWindow()
    {
        var window = ActiveChatWindow;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        if (CurrentRun is { Activated: { } ready } run)
        {
            run.Activated = null;
            ready.TrySetResult();
            RenderChatList();
        }
        PlaceQuestion();
        if (window.Topmost) return;
        window.Topmost = true;
        window.Topmost = false;
    }

    void CompactMode_Click(object sender, RoutedEventArgs e)
    {
        if (VoiceShown || _talkClosing || !MicButton.IsEnabled)
        {
            ShowToast(Tr("Завершите голосовой ввод перед переключением окна.", "Finish voice input before switching windows."), ToastKind.Warning);
            return;
        }
        MoreMenu.IsOpen = false;
        CloseFlyout();
        CloseOverlay();
        SaveState();
        var compact = _compactChat = new CompactChatWindow(AnswerView) { Draft = InputBox.Text };
        compact.FontSize = 14 + ChatTextSize.Extra;
        compact.ChangesPill.Click += ChangesPill_Click;
        compact.DraftChanged += ComposerDraftChanged;
        compact.Send += () => _ = SendAsync();
        compact.ActivateAction += UseComposerAction;
        compact.Stop += StopAgent;
        compact.EndTalk += EndTalk;
        compact.TalkPanel.Click += TalkPanel_Click;
        compact.DismissToast += HideToast;
        compact.ToastAction.Click += ToastAction_Click;
        compact.ChooseFiles += PickAttachmentFiles;
        compact.TalkScale.ScaleX = compact.TalkScale.ScaleY = _talkScaleTarget;
        compact.TalkWave.IsThinking = TalkWave.IsThinking;
        AutomationProperties.SetItemStatus(compact.TalkPanel, AutomationProperties.GetItemStatus(TalkPanel));
        compact.ShowAttachments(_pendingAttachments, RemovePendingAttachment);
        compact.AddHandler(CommandManager.PreviewCanExecuteEvent, new CanExecuteRoutedEventHandler(InputBox_PreviewCanExecute));
        compact.AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(InputBox_PreviewExecuted));
        compact.PreviewDragOver += (_, e) =>
        {
            if (!HasFiles(e.Data)) return;
            e.Handled = true;
            bool accepted = _chatOperation == null;
            e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            compact.ShowAttachmentDrop(accepted);
        };
        compact.PreviewDragLeave += (_, _) => compact.ShowAttachmentDrop(false);
        compact.PreviewDrop += (_, e) =>
        {
            compact.ShowAttachmentDrop(false);
            if (!HasFiles(e.Data)) return;
            e.Handled = true;
            if (WaitForChatFile(_chatOperation != null)) return;
            _ = AddAttachmentsAsync(AttachmentDropPaths(e.Data));
            compact.FocusInput();
        };
        compact.Closed += (_, _) =>
        {
            if (_compactChat != compact) return;
            compact.QuestionSlot.Content = null;
            _compactChat = null;
            if (Dispatcher.HasShutdownStarted || _installingUpdate) return;
            var draft = compact.Draft.ReplaceLineEndings("\n");
            if (InputBox.Text != draft) ComposerDraft = draft;
            ActivateChatWindow();
            if (CurrentRun?.Question == null) InputBox.Focus();
        };
        SyncCompactChat();
        UpdateChangesPill();
        UpdateCompactAction();
        if (_lastErrorCard?.Tag is string error) compact.ShowError(error);
        if (_cts != null)
        {
            UpdateWorkStatus();
            if (!_draftIsProgress) compact.ShowDraft(_draftText.ToString());
        }
        compact.Show();
        // Only Computer Use needs this: its screenshots must show the desktop behind the window.
        if (_toolContext is { ComputerUse: true, SandboxRoot: null }) WindowEffects.ExcludeFromCapture(new WindowInteropHelper(compact).Handle);
        Hide();
        ActivateChatWindow();
        if (CurrentRun?.Question == null) compact.FocusInput();
    }

    void SyncCompactChat()
    {
        if (_compactChat == null) return;
        var history = _agent.History;
        List<JsonObject> messages = [];
        for (int i = history.Count - 1; i >= 0 && messages.Count < 16; i--)
        {
            if (history[i] is not JsonObject message) continue;
            var role = AgentRunner.Role(message);
            if (role == "user" || role == "assistant" && message["tool_calls"] == null && AgentRunner.ReviewOf(message) == null &&
                !string.IsNullOrWhiteSpace(AgentRunner.ExtractText(message["content"])))
                messages.Add(message);
        }
        messages.Reverse();
        _compactChat.SyncHistory(messages.ToArray());
    }

    void UpdateCompactAction()
    {
        if (_compactChat is not { } compact) return;
        compact.ComposerLoadingStatus.Content = ComposerLoadingStatus.Content is ComposerLoad { IsIndexing: false } load ? load : null;
        compact.UpdateAction(SendButton, StopButton, _talk != null);
    }

    void PlaceQuestion()
    {
        QuestionHost.Content = null;
        QuestionHost.Visibility = Visibility.Collapsed;
        var host = _compactChat?.QuestionSlot ?? QuestionHost;
        var run = CurrentRun;
        host.Content = run?.Question;
        host.Visibility = VisibleIf(run?.Question != null);
        UpdateWorkStatus();
        UpdateChangesPill();
        if (run?.QuestionFocus is { } focus)
            _ = Dispatcher.InvokeAsync(() => { if (CurrentRun?.QuestionFocus == focus) focus.Focus(); }, DispatcherPriority.Input);
    }
}
