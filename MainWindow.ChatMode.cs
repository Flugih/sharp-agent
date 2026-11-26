namespace SharpAgent;

public partial class MainWindow
{
    void TemporaryChat_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsOpen || WaitForChatFile(ChatFileBusy))
        {
            TemporaryChatButton.IsChecked = _agent.IsTemporary;
            return;
        }
        CloseOverlay();
        if (_agent.IsTemporary) StartNewChat();
        else SelectChat(CreateAgent(temporary: true));
        InputBox.Focus();
    }

    async void SaveTemporaryChat_Click(object sender, RoutedEventArgs e)
    {
        var agent = _agent;
        if (agent is not { IsTemporary: true, History.Count: > 0 } || WaitForChatFile(ChatFileBusy)) return;
        SaveTemporaryChatButton.IsEnabled = false;
        try
        {
            await RunChatOperationAsync(() => agent.SaveAsync(saveTemporary: true));
            if (agent.IsTemporary) return;
            if (_agent == agent)
            {
                UpdateChatMode();
                SaveState();
                ShowToast(Tr("Чат сохранён", "Chat saved"), ToastKind.Success);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(Tr("Не удалось сохранить чат: ", "Could not save chat: ") + ex.Message, ToastKind.Error);
        }
        finally { SaveTemporaryChatButton.IsEnabled = true; }
    }

    async void ChatMode_Click(object sender, RoutedEventArgs e)
    {
        if (_agent == null) return;
        if (_cts != null || ChatFileBusy)
        {
            UpdateChatMode();
            return;
        }
        await _agent.SetWorkModeAsync(sender == WorkModeButton);
        UpdateChatMode();
        InputBox.Focus();
    }

    void UpdateChatMode()
    {
        bool work = _agent.WorkMode;
        ConversationModeButton.IsChecked = !work;
        WorkModeButton.IsChecked = work;
        TemporaryChatButton.IsChecked = _agent.IsTemporary;
        WorkContextPanel.Visibility = VisibleIf(work);
        InputHint.Text = work ? Tr("Работайте в Sharp Agent", "Work with Sharp Agent") : Tr("Обсудить с Sharp Agent", "Discuss with Sharp Agent");
        ComposerIntro.Text = work ? Tr("Над чем поработаем?", "What shall we work on?") : Tr("Что обсудим?", "What’s on your mind?");
        RenderNextActions();
        UpdateProjectWatcher();
        UpdateSendButton();
        UpdateComposerPlacement();
    }

    void UpdateProjectWatcher()
    {
        if (!_startupChatReady) return;
        if (_agent.WorkMode) _toolContext.ProjectWatcher.Watch(_toolContext);
        else _toolContext.ProjectWatcher.Dispose();
    }

    void ComposerPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged) return;
        double inset = ComposerPanel.ActualHeight + ComposerPanel.Margin.Bottom;
        ChatScroll.Padding = new(0, 0, 0, inset);
        ChatContent.Margin = ChatContent.Margin with { Bottom = inset + 24 };
        ScrollDownButton.Margin = new(0, 0, 0, inset + 12);
    }

    void UpdateComposerPlacement()
    {
        bool loading = IsShown(ChatSkeleton);
        bool empty = !loading && _agent.History.Count == 0 && ChatPanel.Children.Count == 0;
        ChatModePicker.Visibility = VisibleIf(empty && _agent.ChatPath == null);
        TemporaryChatButton.Visibility = VisibleIf(empty && _agent.ChatPath == null);
        SaveTemporaryChatButton.Visibility = VisibleIf(_agent.IsTemporary && !empty && !loading);
        double top = IsShown(ChatModePicker) || IsShown(SaveTemporaryChatButton) ? 58 : 12;
        if (ChatContent.Margin.Top != top || ChatStickyHost.Margin.Top != top)
        {
            ChatContent.Margin = ChatContent.Margin with { Top = top };
            ChatStickyHost.Margin = ChatStickyHost.Margin with { Top = top };
            UpdateStickyClip();
        }
        ComposerIntro.Visibility = VisibleIf(empty);
        Grid.SetRow(ComposerHost, empty ? 1 : 2);
        Grid.SetRowSpan(ComposerHost, empty ? 2 : 1);
        ComposerPanel.VerticalAlignment = empty ? VerticalAlignment.Center : VerticalAlignment.Bottom;
    }
}
