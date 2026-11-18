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
        if (VoiceShown || !MicButton.IsEnabled)
        {
            ShowToast("Завершите голосовой ввод перед переключением окна.", ToastKind.Warning);
            return;
        }
        MoreMenu.IsOpen = false;
        CloseFlyout();
        CloseOverlay();
        SaveState();
        var compact = _compactChat = new CompactChatWindow(AnswerView, path => FileCard(path, null, true)) { Draft = InputBox.Text };
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
            compact.SetStatus(_questionCard == null ? "Думаю…" : "Жду ответа");
            if (!_draftIsProgress) compact.ShowDraft(_draftText.ToString());
        }
        else if (_pendingAttachments.Count > 0) compact.SetStatus($"Вложений к сообщению: {_pendingAttachments.Count}");
        compact.Show();
        // Only Computer Use needs this: its screenshots must show the desktop behind the window.
        if (_toolContext is { ComputerUse: true, SandboxRoot: null }) WindowEffects.ExcludeFromCapture(compact);
        Hide();
        ActivateChatWindow();
        PlaceQuestion();
        if (_questionCard == null) compact.FocusInput();
    }

    void SyncCompactChat()
    {
        if (_compactChat == null) return;
        var history = _agent.History;
        List<(JsonObject Message, string? File)> entries = [];
        var functions = new Dictionary<string, JsonObject>();
        var turnPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int firstResult = history.Count, messages = 0;
        for (int i = history.Count - 1; i >= 0 && messages < 16; i--)
        {
            if (history[i] is not JsonObject message) continue;
            var role = AgentRunner.Role(message);
            if (role == "user" || role == "assistant" && message["tool_calls"] == null && AgentRunner.ReviewOf(message) == null &&
                !string.IsNullOrWhiteSpace(AgentRunner.ExtractText(message["content"])))
            {
                entries.Add((message, null));
                messages++;
            }
            if (role == "user") turnPaths.Clear();
            if (role != "tool" || AgentRunner.FileChangeUndone(message)) continue;
            var output = JsonUtil.Str(message["content"]) ?? "";
            if (ToolRegistry.IsErrorOutput(output)) continue;
            var path = AgentRunner.FileChangeOf(message)?.Path ?? JsonUtil.Str(message[AgentRunner.MetaField]?["written_path"]);
            if (path == null)
            {
                if (i < firstResult)
                {
                    firstResult = i;
                    while (firstResult > 0 && AgentRunner.Role(history[firstResult - 1]) == "tool") firstResult--;
                    functions.Clear();
                    if (firstResult > 0 && history[firstResult - 1]["tool_calls"] is JsonArray calls)
                        foreach (var call in calls.OfType<JsonObject>())
                            if (JsonUtil.Str(call["id"]) is { } id && call["function"] is JsonObject fn) functions[id] = fn;
                }
                if (!functions.TryGetValue(JsonUtil.Str(message["tool_call_id"]) ?? "", out var function)) continue;
                var name = JsonUtil.Str(function["name"]) ?? "";
                var args = JsonUtil.Str(function["arguments"]) ?? "{}";
                path = Produced(name, args, name is "write_file" or "edit_file" ? ResultPath(output) : null, output)?.Path;
            }
            if (path != null && turnPaths.Add(path) && File.Exists(path)) entries.Add((message, path));
        }
        entries.Reverse();
        _compactChat.SyncHistory(entries.ToArray());
    }

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
