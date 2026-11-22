namespace SharpAgent;

public partial class MainWindow
{
    sealed class ChatRun(AgentRunner agent, Action prepare)
    {
        public readonly CancellationTokenSource Cancellation = new();
        public readonly ChatHistory.View History = agent.HistoryView;
        public readonly List<Action> Events = [];
        public readonly Action Prepare = prepare;
        public Action? Progress;
        public StringBuilder? Delta;
        public bool DeltaUsesTools;
        public TaskCompletionSource? Activated;
        public FrameworkElement? Question, QuestionFocus;
        public bool LimitDeclined, ComputerUsing;
    }

    readonly Dictionary<AgentRunner, ChatRun> _runs = [];
    readonly Dictionary<string, (string Message, ErrorFix Fix)> _chatErrors = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (string Text, Attachment[] Files)> _chatDrafts = new(StringComparer.OrdinalIgnoreCase);
    ComputerUseOverlay? _computerOverlay;
    ChatRun? CurrentRun => _agent != null ? _runs.GetValueOrDefault(_agent) : null;
    CancellationTokenSource? _cts => CurrentRun?.Cancellation;
    bool AnyRunning => _runs.Count > 0;

    AgentRunner CreateAgent(bool temporary = false)
    {
        var agent = new AgentRunner(new ToolRegistry(_toolContext), new ChatStore(AppPaths.HistoryFolder, AppPaths.LogsFolder))
        {
            Api = _provider, Provider = _config.Provider, MaxTokens = _config.MaxTokens,
            AutoChatTitles = _config.AutoChatTitles,
            IsTemporary = temporary,
            RussianLanguage = _config.RussianLanguage,
            FastMode = _state.FastMode, UseSubagents = _config.UseSubagents, ReasoningEffort = _agent?.ReasoningEffort,
            ModelSupportsVision = id => _models.GetValueOrDefault(id)?.Vision, ModelContextTokens = id => _models.GetValueOrDefault(id)?.ContextTokens,
            EconomyModel = () => _state.EconomyModels?.GetValueOrDefault(_config.Provider) is { } candidate &&
                _models.GetValueOrDefault(candidate)?.Chat == true &&
                (_usage.RateOf(candidate) is not { } rate || _usage.RateOf(CurrentModel()) is not { } current || rate <= current) ? candidate : null,
            LocalModel = () => _config.LocalModel,
            SubagentEffort = id => EffortLevelsOf(id).FirstOrDefault(level => level is "minimal" or "low"),
        };
        agent.ConfirmAsync = async request =>
        {
            if (_confirmOff && !request.AlwaysAsk) return true;
            await WaitForActiveChatAsync(agent, _runs[agent].Cancellation.Token);
            return await ConfirmAsync(request);
        };
        agent.ClarifyAsync = (question, ct) => AskClarificationAsync(agent, question, ct);
        agent.CanSpendAsync = () => CheckLimitsAsync(agent);
        agent.AssistantText += (text, elapsed, sentAt) =>
        {
            var actions = elapsed != null && agent.History.Count > 0 ? AgentRunner.NextActionsOf(agent.History[^1]) : [];
            Publish(agent, () => OnAssistantText(text, elapsed, sentAt, actions));
        };
        agent.AssistantDelta += (text, usesTools) => PublishDelta(agent, text, usesTools);
        agent.ProgressReported += progress => Publish(agent, () => OnModelProgress(progress), progress: true);
        agent.ToolCallStarted += call => Publish(agent, () => OnToolCallStarted(call));
        agent.ToolCallFinished += result =>
        {
            long finishedAt = Environment.TickCount64;
            Publish(agent, () => OnToolCallFinished(result, finishedAt));
        };
        agent.CompactionStarted += () => Publish(agent, ShowCompacting);
        agent.PreparingContext += status => Publish(agent, () => ShowPreparingContext(status), progress: true);
        agent.ThinkingStarted += () => Publish(agent, ShowThinking);
        agent.SubagentChanged += update => Publish(agent, () => OnSubagentChanged(update));
        agent.ComputerUseStarted += () =>
        {
            if (!_runs.TryGetValue(agent, out var run) || run.ComputerUsing) return;
            run.ComputerUsing = true;
            UpdateComputerUseOverlay();
        };
        agent.UsageReported += (model, usage) =>
        {
            if (_config.Provider == AgentProvider.Tooken) _usage.AddLocal(model, usage.PromptTokens + usage.CompletionTokens);
            UpdateBalanceIndicator();
            if (_agent == agent) UpdateContextBanner();
        };
        agent.Warning += text => Publish(agent, () => AddNotice(text, true));
        agent.Stopped += text => Publish(agent, () => ShowStop(text));
        agent.Notice += text => Publish(agent, () => AddNotice(text));
        agent.Retrying += (attempt, total) => Publish(agent, () => ShowReconnect(attempt, total));
        agent.ChatTitleChanged += () => SyncChat(agent, touched: false);
        agent.ChatCreated += () =>
        {
            if (_agent == agent)
            {
                _state.CurrentChat = Path.GetFileName(agent.ChatPath);
                _state.Save();
            }
            SyncChat(agent, touched: true);
        };
        return agent;
    }

    AgentRunner? RunningChat(string path) =>
        _runs.Keys.FirstOrDefault(agent => agent.ChatPath is { } file && PathHelper.SamePath(path, file));

    void UpdateComputerUseOverlay()
    {
        if (_runs.Values.FirstOrDefault(run => run.ComputerUsing) is { } active)
            _computerOverlay ??= new(Dispatcher, () =>
            {
                if (!active.ComputerUsing || active.Cancellation.IsCancellationRequested) return;
                if (CurrentRun == active) StopAgent();
                else active.Cancellation.Cancel();
            });
        else
        {
            _computerOverlay?.Dispose();
            _computerOverlay = null;
        }
    }

    void SelectChat(AgentRunner agent)
    {
        bool leavingTemporary = _agent.IsTemporary && _agent != agent;
        var abandoned = leavingTemporary ? CurrentRun : null;
        if (!_agent.IsTemporary)
        {
            var previous = _agent.ChatPath ?? "";
            if (InputBox.Text.Length > 0 || _pendingAttachments.Count > 0)
                _chatDrafts[previous] = (InputBox.Text, _pendingAttachments.ToArray());
            else _chatDrafts.Remove(previous);
        }
        _agent = agent;
        abandoned?.Cancellation.Cancel();
        if (leavingTemporary && VoiceShown) CancelVoice();
        var draft = agent.IsTemporary ? default : _chatDrafts.GetValueOrDefault(agent.ChatPath ?? "");
        InputBox.Text = draft.Text ?? "";
        InputBox.CaretIndex = InputBox.Text.Length;
        _pendingAttachments.Clear();
        if (draft.Files != null) _pendingAttachments.AddRange(draft.Files);
        RenderPendingAttachments();
        _state.CurrentChat = Path.GetFileName(agent.ChatPath);
        RenderCurrentChat();
    }

    void PublishDelta(AgentRunner agent, string? text, bool usesTools)
    {
        if (!_runs.TryGetValue(agent, out var run)) return;
        if (text == null)
        {
            Publish(agent, () => OnAssistantDelta(null, false));
            return;
        }
        else
        {
            if (run.Progress is { } latest) run.Events.Add(latest);
            run.Progress = null;
            (run.Delta ??= new()).Append(text);
            run.DeltaUsesTools |= usesTools;
        }
        if (_agent == agent) OnAssistantDelta(text, usesTools);
    }

    void FlushDelta(ChatRun run)
    {
        if (run.Delta is not { } delta) return;
        var text = delta.ToString();
        bool usesTools = run.DeltaUsesTools;
        run.Events.Add(() => OnAssistantDelta(text, usesTools));
        run.Delta = null;
        run.DeltaUsesTools = false;
    }

    void Publish(AgentRunner agent, Action action, bool progress = false)
    {
        if (!_runs.TryGetValue(agent, out var run)) return;
        FlushDelta(run);
        if (progress) run.Progress = action;
        else
        {
            if (run.Progress is { } latest) run.Events.Add(latest);
            run.Progress = null;
            run.Events.Add(action);
        }
        if (_agent == agent) action();
    }

    void RestoreRunningChat()
    {
        if (CurrentRun is not { } run) return;
        _suppressAnimation = true;
        try
        {
            run.Prepare();
            StartProgress();
            foreach (var action in run.Events) action();
            run.Progress?.Invoke();
            if (run.Delta is { } delta) OnAssistantDelta(delta.ToString(), run.DeltaUsesTools);
            _draftTimer?.Stop();
            if (_draftText.Length > 0) RenderDraft();
        }
        finally { _suppressAnimation = false; }
        UpdateWorkStatus();
        run.Activated?.TrySetResult();
        run.Activated = null;
    }

    async Task WaitForActiveChatAsync(AgentRunner agent, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_agent == agent && ActiveChatWindow.IsVisible)
            {
                if (_chatOperation is not { IsCompleted: false } operation) return;
                await operation.WaitAsync(ct);
                continue;
            }
            var run = _runs[agent];
            var ready = run.Activated ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            RenderChatList();
            await ready.Task.WaitAsync(ct);
        }
    }

    void ReportRunError(AgentRunner agent, string message, ErrorFix fix)
    {
        if (agent.ChatPath is { } path) _chatErrors[path] = (message, fix);
        if (_agent == agent) AddError(message, fix);
    }
}
