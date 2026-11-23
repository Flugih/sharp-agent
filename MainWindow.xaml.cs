using System.Windows.Shell;

namespace SharpAgent;

public partial class MainWindow
{
    static readonly string[] DefaultModels =
    [
        "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5", "claude-fable-5-2", "claude-opus-4-5", "gpt-6.1-sol", "gpt-6.1-terra",
        "gpt-6-luna", "gpt-6-astra", "deepseek-v4-pro", "deepseek-v4.1-flash-1017", "kimi-k3.1-pro", "glm-5.3", "glm-5.3-flash",
        "gemini-4-argon", "gemini-3.8-flash",
    ];

    static readonly string[] ChatGptModels =
        ["gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-6.1-terra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5"];

    static readonly string[] GroqModels = ["openai/gpt-oss-120b", "openai/gpt-oss-20b", "qwen/qwen3.8-27b"];

    static readonly Dictionary<string, (string Russian, string English, string RussianHint, string EnglishHint)> EffortNames = new()
    {
        ["minimal"] = ("Минимальное", "Minimal", "почти без рассуждений", "Very little reasoning"),
        ["low"] = ("Низкое", "Low", "быстрее и дешевле", "Faster and cheaper"),
        ["medium"] = ("Среднее", "Medium", "баланс скорости и тщательности", "Balances speed and thoroughness"),
        ["high"] = ("Высокое", "High", "тщательнее, но дольше и дороже", "More thorough, slower and more costly"),
        ["xhigh"] = ("Очень высокое", "Very high", "для сложных задач, ещё дольше", "Takes longer for complex tasks"),
        ["max"] = ("Максимальное", "Maximum", "почти предельная тщательность", "Near maximum thoroughness"),
        ["ultra"] = ("Ультра", "Ultra", "предельная тщательность, дольше всего", "Most thorough and slowest"),
    };

    readonly ToolContext _toolContext = new()
    {
        AppFolder = AppPaths.AppFolder,
        InstallationFolder = !PathHelper.IsInside(AppPaths.AppFolder, AppContext.BaseDirectory) ? Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) : null,
        LogsFolder = AppPaths.LogsFolder,
    };
    readonly UiState _state = UiState.Load();
    readonly ChatLinks _links;
    readonly MarkdownRenderer _markdown;
    readonly UsageTracker _usage;
    readonly ChatGptAuth _chatGpt = new(AppPaths.ChatGptAuthPath);
    readonly Dictionary<AgentProvider, List<string>> _modelLists = [];
    readonly DispatcherTimer _subagentTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _cacheTimer = new() { Interval = TimeSpan.FromHours(6) };
    Dictionary<string, ModelInfo> _models = new(StringComparer.OrdinalIgnoreCase);
    List<string>? _imageModels;
    AppConfig _config = null!;
    AgentRunner _agent = null!;
    ChatApiClient? _tooken;
    IChatProvider? _provider;
    bool _confirmOff;

    public MainWindow()
    {
        ApplyTheme();
        InitializeComponent();
        ChatTextSize.Extra = _state.LargeChatText ? 1 : 0;
        InputBox.FontSize = 14 + ChatTextSize.Extra;
        _links = new(this, new PathRefs(() => _toolContext.WorkingDirectory), OpenInApp);
        _toolContext.OpenUrl = OpenBrowserUrl;
        _toolContext.BrowserWindow = () => Dispatcher.Invoke(BrowserWindowHandle);
        _toolContext.ReadBrowser = ReadBrowserAsync;
        _markdown = new(_links.Markdown);
        _usage = new(_state);
        _toolContext.ProjectWatcher.ProgressChanged += OnIndexProgressChanged;
        ChatScroll.PreviewMouseWheel += ChatScroll_PreviewMouseWheel;
        Action<string, bool> notice = (text, error) => ShowToast(text, error ? ToastKind.Error : ToastKind.Info);
        FileActions.Notify += notice;
        Closed += (_, _) =>
        {
            _toolContext.ProjectWatcher.ProgressChanged -= OnIndexProgressChanged;
            _toolContext.ProjectWatcher.Dispose();
            _cacheTimer.Stop();
            _recorder?.Cancel();
            CloseOfficePreview();
            _computerOverlay?.Dispose();
            _computerOverlay = null;
            ResetBrowserNetwork(closing: true);
            FileActions.Notify -= notice;
            var compact = _compactChat;
            _compactChat = null;
            compact?.Close();
        };
        _subagentTimer.Tick += (_, _) => UpdateSubagentTimes();
        _cacheTimer.Tick += (_, _) => _ = Task.Run(AppCache.Maintain);
        _voiceTimer.Tick += (_, _) => OnVoiceTick();
        RestoreWindowPlacement();
    }

    // The installed build keeps its data in %LOCALAPPDATA%, not next to the program.
    static string FolderKind => AppPaths.IsInstalled ? Tr("данных", "data") : Tr("программы", "application");

    static Visibility VisibleIf(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    static bool IsShown(UIElement element) => element.Visibility == Visibility.Visible;

    static void SetHint(FrameworkElement element, string hint)
    {
        element.ToolTip = hint;
        AutomationProperties.SetName(element, hint);
    }

    async void Window_Loaded(object s, RoutedEventArgs e)
    {
        if (_agent != null) return;
        _config = AppConfig.Load();
        _agent = CreateAgent();
        ApplyConfig();
        UpdateConfirmToggle();
        ApplySidebar();
        var startupChat = OpenStartupChatAsync();
        UpdateTrashCount();
        _ = LoadChatsAsync();
        _ = Task.Run(AppCache.Maintain);
        _cacheTimer.Start();
        if (_config.LoadError != null)
            FolderBanner("env", Tr("Не удалось прочитать файл настроек .env", "Could not read the .env settings file"), Tr("Пока действуют настройки по умолчанию, а сеть отключена, чтобы запросы не прошли мимо выбранного VPN или прокси. Причина: ", "Default settings are in effect. Networking is disabled to keep requests from bypassing your VPN or proxy. Reason: ") + _config.LoadError);
        if (!AppPaths.IsWritable(AppPaths.AppFolder))
            FolderBanner("readonly", Tr($"Папка {FolderKind} недоступна для записи", $"The {FolderKind} folder is read-only"), Tr("Настройки и переписка не сохранятся. ", "Settings and conversations cannot be saved. ") +
                (AppPaths.IsInstalled ? Tr("Проверьте права доступа к этой папке.", "Check the permissions for this folder.") : Tr("Перенесите папку Sharp Agent, например, на Рабочий стол.", "Move the Sharp Agent folder to a location you can write to, such as the Desktop.")));
        UpdateSendButton();
        if (_provider != null) InputBox.Focus();
        await startupChat;
        if (_provider == null) ShowSettings();
        void FolderBanner(string key, string title, string text) =>
            ShowBanner(key, Glyphs.Warning, title, text, Tr("Открыть папку", "Open folder"), () => FileActions.OpenFolder(AppPaths.AppFolder), true);
    }

    void Window_Closing(object? s, CancelEventArgs e)
    {
        e.Cancel = true;
        SaveState();
        Dispatcher.InvokeAsync(() => { if (IsVisible) Hide(); });
    }

    void SaveState()
    {
        _state.Window = CapturePlacement();
        _state.Draft = _agent?.IsTemporary != true && ComposerDraft is { Length: > 0 } draft ? draft.ReplaceLineEndings("\n") : null;
        _state.CurrentChat = Path.GetFileName(_agent?.ChatPath);
        _state.Save();
    }

    void ApplyConfig()
    {
        UiLanguage.Current.Value.Apply(_config.RussianLanguage);
        bool routeChanged = _browserNetwork != _config.Network;
        if (routeChanged) ResetBrowserNetwork();
        AppNetwork.Apply(_config.Network);
        _browserNetwork = _config.Network;
        if (routeChanged && _browserTab != null && IsShown(BrowserPane)) SelectBrowserTab(_browserTab);
        _agent.MaxTokens = _config.MaxTokens;
        _agent.AutoChatTitles = _config.AutoChatTitles;
        _agent.RussianLanguage = _config.RussianLanguage;
        _toolContext.SandboxRoot = _config.Sandbox ? AppPaths.AppFolder : null;
        _toolContext.ComputerUse = _config.ComputerUse;
        ApplyWorkDir();
        ApplyProvider();
        ApplyFastMode();
        ModeText.Text = _config.Sandbox ? Tr("Песочница", "Sandbox") : Tr("Весь ПК", "Full computer access");
        ModeIcon.Text = _config.Sandbox ? Glyphs.Lock : Glyphs.Laptop;
        ModePill.ToolTip = _config.Sandbox
            ? Tr($"Агент работает только внутри папки {FolderKind}:\n{AppPaths.AppFolder}\nКоманды PowerShell и cmd отключены.",
                $"The agent can only work in the {FolderKind} folder:\n{AppPaths.AppFolder}\nPowerShell and cmd commands are disabled.")
            : Tr("Агент работает с любыми файлами пользователя, без прав администратора.", "The agent can access your files using your current account permissions.");
        AutomationProperties.SetName(ModePill, Tr($"Режим: {ModeText.Text}. Открыть настройки", $"Mode: {ModeText.Text}. Open settings"));
    }

    public void ApplyTheme()
    {
        if (!Theme.Apply(Application.Current.Resources, _state.Theme)) return;
        WindowEffects.ApplyFrame(this, ((SolidColorBrush)Theme.Line).Color);
        if (_config == null) return;
        UpdateBalanceIndicator();
    }

    string? SaveEnv(Dictionary<string, string?> values)
    {
        try
        {
            if (!File.Exists(AppPaths.EnvPath)) File.WriteAllText(AppPaths.EnvPath, AppConfig.EnvTemplate);
            DotEnv.Save(AppPaths.EnvPath, values, AppPaths.ProtectSecrets);
            _config = AppConfig.Load();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }

    void ApplyWorkDir()
    {
        var folder = _toolContext.WorkingDirectory = _config.WorkingDirectory;
        _toolContext.ProjectWatcher.Watch(_toolContext);
        UpdateSendButton();
        var dir = Path.TrimEndingDirectorySeparator(folder);
        FolderText.Text = Path.GetFileName(dir) is { Length: > 0 } name ? name : dir;
        FolderButton.ToolTip = folder + Tr("\nОткрыть в Проводнике (Ctrl+Shift+O)", "\nOpen in File Explorer (Ctrl+Shift+O)");
        AutomationProperties.SetName(FolderButton, Tr($"Рабочая папка {FolderText.Text}. Выбрать другую", $"Working folder: {FolderText.Text}. Choose another folder"));
        GitBranchButton.Visibility = Visibility.Collapsed;
        _ = RefreshGitBranchAsync();
    }

    void FolderButton_Click(object s, RoutedEventArgs e)
    {
        if (WaitForChatFile(ChatFileBusy)) return;
        if (AnyRunning)
        {
            ShowToast(Tr("Папку можно сменить, когда агент закончит работу", "You can change the working folder after the agent finishes."), ToastKind.Warning);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Tr("Рабочая папка агента", "Agent working folder") };
        if (Directory.Exists(_toolContext.WorkingDirectory)) dialog.InitialDirectory = _toolContext.WorkingDirectory;
        if (dialog.ShowDialog(this) != true) return;
        var folder = dialog.FolderName;
        bool inside = PathHelper.IsInside(folder, AppPaths.AppFolder);
        if (_config.Sandbox && !inside)
        {
            ShowToast(Tr($"В песочнице агенту доступна только папка {FolderKind}. Выберите папку внутри неё или выключите песочницу в настройках.", $"Sandbox access is limited to the {FolderKind} folder. Choose a folder within it or turn off sandbox mode in settings."),
                ToastKind.Warning);
            return;
        }
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            ShowToast(Tr("Эта папка недоступна: ", "This folder is unavailable: ") + ex.Message, ToastKind.Warning);
            return;
        }
        if (SaveEnv(new() { ["AGENT_WORKDIR"] = inside ? Path.GetRelativePath(AppPaths.AppFolder, folder) : folder }) is { } error)
        {
            ShowToast(Tr("Не удалось сохранить выбор в .env: ", "Could not save the selection to .env: ") + error, ToastKind.Error);
            return;
        }
        ApplyWorkDir();
        ShowToast(Tr("Агент работает в папке ", "Working folder: ") + _toolContext.WorkingDirectory, ToastKind.Success);
    }

    void OpenWorkDir_Click(object s, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_toolContext.WorkingDirectory); }
        catch (Exception ex)
        {
            ShowToast(Tr("Не удалось создать рабочую папку: ", "Could not create working folder: ") + ex.Message, ToastKind.Error);
            return;
        }
        FileActions.OpenFolder(_toolContext.WorkingDirectory);
    }

    void ModePill_Click(object s, RoutedEventArgs e) => ShowSettings();

    void ConfirmToggle_Checked(object s, RoutedEventArgs e)
    {
        _confirmOff = false;
        if (IsLoaded) UpdateConfirmToggle();
    }

    void ConfirmToggle_Unchecked(object s, RoutedEventArgs e)
    {
        if (!DarkDialog.Ask(this, Tr("Отключить подтверждения?", "Turn off confirmations?"),
                Tr("Агент сможет удалять и перезаписывать файлы и выполнять команды, ни о чём не спрашивая. Подтверждения снова включатся при следующем запуске программы.",
                    "The agent will be able to delete or overwrite files and run commands without asking. Confirmations will turn back on when you restart the app."),
                Tr("Отключить", "Turn off"), danger: true))
        {
            ConfirmToggle.IsChecked = true;
            return;
        }
        _confirmOff = true;
        UpdateConfirmToggle();
    }

    void UpdateConfirmToggle()
    {
        bool on = !_confirmOff;
        ConfirmIcon.Text = on ? Glyphs.Shield : Glyphs.Warning;
        ConfirmLabel.Text = on ? Tr("Подтверждения", "Confirmations") : Tr("Без подтверждений", "Confirmations off");
        ConfirmToggle.ToolTip = on
            ? Tr("Агент спрашивает разрешение перед удалением, перезаписью файлов и запуском команд.", "The agent asks before deleting or overwriting files and running commands.")
            : Tr("Агент удаляет, перезаписывает файлы и запускает команды без вопросов.", "The agent can delete or overwrite files and run commands without asking.");
        AutomationProperties.SetName(ConfirmToggle, on ? Tr("Подтверждения включены", "Confirmations on") : Tr("Подтверждения выключены", "Confirmations off"));
        ConfirmToggle.Paint(ForegroundProperty, on ? "TextDim" : "Warning");
    }

    Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        if (_confirmOff && !request.AlwaysAsk) return Task.FromResult(true);
        SetTaskbar(TaskbarItemProgressState.Paused);
        var owner = ActiveChatWindow;
        WindowEffects.Flash(owner);
        bool allowed = DarkDialog.Ask(owner, request.Title, request.Note ?? "", request.Action, Tr("Отклонить", "Decline"), request.Danger, request.Subject, true);
        SetTaskbar(TaskbarItemProgressState.Indeterminate);
        return Task.FromResult(allowed);
    }

    void Send_Click(object s, RoutedEventArgs e) => _ = SendAsync();

    void Stop_Click(object s, RoutedEventArgs e) => StopAgent();

    void StopAgent()
    {
        if (_cts is not { IsCancellationRequested: false }) return;
        _cts.Cancel();
        StopButton.IsEnabled = false;
        UpdateChangesPill();
        UpdateCompactAction();
    }

    void InputBox_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        _ = SendAsync();
    }

    void InputBox_TextChanged(object s, TextChangedEventArgs e)
    {
        UpdateInputHint();
        if (_compactChat == null) ComposerDraftChanged();
    }

    void UpdateInputHint() => InputHint.Visibility = VisibleIf(InputBox.Text.Length == 0 && IsShown(InputBox));

    bool _composerHasText;

    void ComposerDraftChanged()
    {
        bool hasText = !string.IsNullOrWhiteSpace(ComposerDraft);
        if (_composerHasText != hasText) UpdateSendButton();
    }

    void UpdateSendButton()
    {
        var loading = UpdateComposerLoading();
        _composerHasText = !string.IsNullOrWhiteSpace(ComposerDraft);
        bool hasDraft = _composerHasText || _pendingAttachments.Count > 0, resume = !hasDraft && _agent?.CanContinue == true;
        SendButton.IsEnabled = loading == null && (hasDraft || resume);
        SendButton.Content = resume ? Glyphs.Play : Glyphs.Send;
        SendButton.ToolTip = loading ?? (resume ? Tr("Продолжить задачу (Enter)", "Resume task (Enter)") : Tr("Отправить (Enter). Новая строка — Shift+Enter", "Send (Enter). New line: Shift+Enter"));
        AutomationProperties.SetName(SendButton, resume ? Tr("Продолжить задачу", "Resume task") : Tr("Отправить", "Send"));
        NextActionPanel.Visibility = VisibleIf(_agent?.WorkMode == true && _cts == null && !hasDraft && !VoiceShown && NextActionPanel.Children.Count > 0);
        UpdateCompactAction();
    }

    void UpdateComposerButtons()
    {
        bool voice = VoiceShown, running = _cts != null;
        SendButton.Visibility = VisibleIf(!voice && !running);
        StopButton.Visibility = VisibleIf(running);
        StopButton.ToolTip = voice ? Tr("Остановить агента", "Stop agent") : Tr("Остановить агента (Esc)", "Stop agent (Esc)");
        MicButton.Visibility = VisibleIf(!voice);
        UpdateSendButton();
    }

    async Task SendAsync()
    {
        if (ChatSubmitBusy) return;
        if (_cts != null)
        {
            ShowToast(Tr("Агент ещё работает. Дождитесь ответа или остановите его (Esc)", "The agent is still working. Wait for the response or stop it (Esc)."), ToastKind.Warning);
            return;
        }
        var text = ComposerDraft.Trim().ReplaceLineEndings("\n");
        var attachments = _pendingAttachments.ToList();
        if (text.Length == 0 && attachments.Count == 0)
        {
            await RetryAsync();
            return;
        }
        bool canRunLocally = attachments.Count == 0 && (LocalActionRouter.Match(text) != null || LocalActionRouter.Diagnosis(text) != null ||
            !_agent.WorkMode && !_config.Sandbox && _config.LocalModel != null && AdaptiveModelRouter.IsSimple(text));
        if ((canRunLocally ? CurrentModel() : ModelForRequest()) is not { } model) return;
        var history = _agent.HistoryView;
        if (Enumerable.Range(_renderedHistory.Count, Math.Max(0, history.Count - _renderedHistory.Count)).Count(history.IsUser) >= HistoryPageSize)
            RenderCurrentChat();
        ComposerDraft = "";
        ClearPendingAttachments();
        ClearSubagents();
        ScrollToLatest();
        var sentAt = DateTimeOffset.UtcNow;
        await RunAgentAsync((agent, ct) => agent.RunAsync(text, attachments, model, ct, sentAt), () => AddUserBubble(text, attachments));
    }

    string? ModelForRequest()
    {
        if (_config.Provider == AgentProvider.ChatGpt && (_signIn != null || _chatGptSigningOut))
        {
            ShowToast(Tr("Дождитесь завершения входа или выхода из ChatGPT.", "Wait for ChatGPT sign-in or sign-out to finish."), ToastKind.Warning);
            return null;
        }
        if (_provider == null)
        {
            ShowToast(NotReadyText(), ToastKind.Warning);
            if (_compactChat == null) ShowSettings();
            return null;
        }
        var model = CurrentModel();
        if (!DrawsOnly(model)) return model;
        ShowToast($"{model} рисует картинки, но не ведёт чат. Выберите текстовую модель — картинку агент нарисует сам.", ToastKind.Warning);
        return null;
    }

    async Task RetryAsync()
    {
        if (ChatSubmitBusy || _cts != null || !_agent.CanContinue ||
            (_agent.PendingLocalCommand != null || _agent.PendingDiagnosis != null ? CurrentModel() : ModelForRequest()) is not { } model) return;
        ScrollToLatest();
        await RunAgentAsync((agent, ct) => agent.ContinueAsync(model, ct), () =>
        {
            RemoveErrorCard();
            HideChangeCards();
            if (_chatView.LastStopNotice != null) ChatPanel.Children.Remove(_chatView.LastStopNotice);
            _chatView.LastStopNotice = null;
        });
    }

    Task RunAgentAsync(Func<AgentRunner, CancellationToken, Task> run, Action prepare)
    {
        var agent = _agent;
        if (agent.ChatPath is { } path) _chatErrors.Remove(path);
        var session = new ChatRun(agent, prepare);
        _runs.Add(agent, session);
        return ExecuteRunAsync(agent, session, run);
    }

    async Task ExecuteRunAsync(AgentRunner agent, ChatRun session, Func<AgentRunner, CancellationToken, Task> run)
    {
        var cts = session.Cancellation;
        using var access = _toolContext.FileAccess.Begin();
        try
        {
            session.Prepare();
            SetRunning(true);
            RenderChatList();
            StartProgress();
            var task = run(agent, cts.Token);
            SyncCompactChat();
            await task;
            if (!ActiveChatWindow.IsActive) WindowEffects.CompletionSound();
            WindowEffects.Flash(ActiveChatWindow);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested || session.LimitDeclined)
        {
            await agent.RecordStopAsync(cts.IsCancellationRequested ? Tr("Агент остановлен.", "Agent stopped.") : Tr("Агент остановлен: исчерпан ваш лимит расхода.", "Agent stopped. Your usage limit has been reached."));
        }
        catch (OperationCanceledException)
        {
            ReportRunError(agent, Tr("Сервер не ответил за отведённое время. Проверьте подключение к интернету и повторите.", "The server did not respond in time. Check your connection and retry."), ErrorFix.Retry);
        }
        catch (ApiException ex)
        {
            if (_provider is ChatGptClient) App.LogError(ex);
            ReportRunError(agent, ex.Message, FixFor(ex));
        }
        catch (NetworkRouteException ex)
        {
            ReportRunError(agent, ex.Message, ErrorFix.Retry);
        }
        catch (HttpRequestException ex)
        {
            ReportRunError(agent, Tr("Нет связи с сервером. Проверьте подключение к интернету и адрес API в настройках.\n",
                "Could not reach the server. Check your internet connection and API address in settings.\n") + ex.Message, ErrorFix.Retry);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ReportRunError(agent, Tr("Не удалось выполнить запрос: ", "Could not complete the request: ") + ex.Message, ErrorFix.Retry);
        }
        finally
        {
            cts.Dispose();
            _runs.Remove(agent);
            if (session.ComputerUsing) UpdateComputerUseOverlay();
            if (_agent == agent)
            {
                ClearDraft(interrupted: true);
                MarkUnfinishedToolRows();
                FinishProgress(Tr("Выполнение прервано", "Interrupted"));
                FlushTurnFiles();
                SyncCompactChat();
                SetRunning(false);
                if (_compactChat == null && (!IsKeyboardFocusWithin || Keyboard.FocusedElement is not TextBoxBase)) ComposerFocus.Focus();
            }
            SyncChat(agent, touched: true);
            if (!AnyRunning) _ = RefreshGitBranchAsync();
            if (_provider is not ChatGptClient)
            {
                _ = RefreshUsageAsync();
                _ = _chatGpt.RenewAsync();
            }
            else if (!AnyRunning && !_chatGpt.PlanAllowed) ApplyProvider();
        }
    }

    void SetRunning(bool running)
    {
        UpdateChangesPill();
        RenderNextActions();
        UpdateComposerButtons();
        StopButton.IsEnabled = _cts?.IsCancellationRequested != true;
        ModelButton.IsEnabled = !running;
        ConversationModeButton.IsEnabled = WorkModeButton.IsEnabled = !running;
        _subagentTimer.IsEnabled = running;
        if (running) SetTaskbar(TaskbarItemProgressState.Indeterminate);
        else if (Taskbar.ProgressState != TaskbarItemProgressState.Error) SetTaskbar(TaskbarItemProgressState.None);
    }

    void SetTaskbar(TaskbarItemProgressState state)
    {
        bool pauseChanged = (Taskbar.ProgressState == TaskbarItemProgressState.Paused) != (state == TaskbarItemProgressState.Paused);
        Taskbar.ProgressState = state;
        Taskbar.ProgressValue = state is TaskbarItemProgressState.Paused or TaskbarItemProgressState.Error ? 1 : 0;
        if (pauseChanged)
        {
            UpdateChangesPill();
            UpdateWorkStatus();
        }
    }

    void Window_PreviewKeyDown(object s, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsShown(SearchLayer)) return;
        if (IsShown(PictureOverlay) && Keyboard.Modifiers == ModifierKeys.None && key is Key.Left or Key.Right)
        {
            MovePicture(key == Key.Left ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (key == Key.Escape)
        {
            if (Keyboard.FocusedElement is TextBox box && ChatList.IsAncestorOf(box)) return;
            if (IsShown(PictureOverlay)) ClosePicture();
            else if (SettingsOpen) CloseSettings();
            else if (IsShown(FlyoutLayer)) CloseFlyout();
            else if (_overlayOpen) CloseOverlay();
            else if (VoiceShown) CancelVoice();
            else if (IsShown(SubagentsWorkspace)) CloseSubagents();
            else if (IsShown(BrowserPane) && (_browserExpanded || BrowserPane.IsKeyboardFocusWithin)) CloseBrowser();
            else if (IsShown(QuestionHost)) StopAgent();
            else if (IsShown(PreviewPane)) ClosePreview();
            else if (_cts != null) StopAgent();
            else return;
            e.Handled = true;
            return;
        }
        if (SettingsOpen) return;
        const ModifierKeys ctrl = ModifierKeys.Control, ctrlShift = ModifierKeys.Control | ModifierKeys.Shift, none = ModifierKeys.None;
        Action? action = (Keyboard.Modifiers, key) switch
        {
            (ctrl, Key.N) => StartNewChat,
            (ctrl, Key.H) => ToggleSidebar,
            (ctrl, Key.K) => OpenSearch,
            (ctrl, Key.O) => () => Attach_Click(this, e),
            (ctrlShift, Key.O) => () => OpenWorkDir_Click(this, e),
            (ctrlShift, Key.C) => () => CopyLastAnswer_Click(this, e),
            (ctrl, Key.B) => () => Balance_Click(this, e),
            (ctrl, Key.OemComma) => ShowSettings,
            (ctrl, Key.L) => () => InputBox.Focus(),
            (none, Key.F1) => () => Shortcuts_Click(this, e),
            (none, Key.F10) => () => More_Click(this, e),
            (none, Key.PageUp or Key.PageDown) when InputBox.LineCount <= 1 && !IsShown(SubagentsWorkspace) &&
                (ReferenceEquals(Keyboard.FocusedElement, InputBox) || Keyboard.FocusedElement is not TextBoxBase) =>
                () => ScrollChatBy((key == Key.PageDown ? 1 : -1) * ChatScroll.ViewportHeight * 0.85),
            _ => null,
        };
        if (action == null) return;
        action();
        e.Handled = true;
    }

    void More_Click(object s, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.IsOpen = true;
    }

    void Shortcuts_Click(object s, RoutedEventArgs e)
    {
        (string Keys, string Action)[] rows =
        [
            ("Enter", Tr("отправить сообщение", "send message")), ("Shift+Enter", Tr("новая строка", "new line")),
            ("Esc", Tr("отменить голосовой ввод, остановить агента, закрыть панель", "cancel voice input, stop the agent, or close the panel")),
            ("Ctrl+N", Tr("новый чат", "new chat")), ("Ctrl+H", Tr("показать или скрыть список чатов", "toggle chat list")), ("F2, Delete", Tr("переименовать, удалить чат в списке", "rename or delete the selected chat")),
            ("Ctrl+K", Tr("поиск по чатам и рабочей папке", "search chats and the working folder")),
            ("Ctrl+O", Tr("прикрепить файлы", "attach files")), ("Ctrl+V", Tr("вставить файлы или скриншот из буфера", "paste files or a screenshot")),
            ("Ctrl+Shift+O", Tr("открыть рабочую папку", "open working folder")), ("Ctrl+Shift+C", Tr("скопировать последний ответ", "copy latest response")), ("Ctrl+B", Tr("баланс и расход", "balance and usage")),
            ("Ctrl+,", Tr("настройки", "settings")), ("Ctrl+L", Tr("перейти в поле ввода", "focus the message box")), ("PageUp, PageDown", Tr("листать переписку", "scroll chat")), ("F10", Tr("другие команды", "more actions")),
        ];
        var grid = new Grid { ColumnDefinitions = { new() { Width = GridLength.Auto }, new() } };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var keys = Theme.KeyBadge(rows[i].Keys);
            keys.Margin = new(0, 0, 16, 8);
            keys.HorizontalAlignment = HorizontalAlignment.Left;
            keys.VerticalAlignment = VerticalAlignment.Top;
            var action = new TextBlock { Text = rows[i].Action, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) }.Paint(TextBlock.ForegroundProperty, "TextSoft");
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }
        DarkDialog.Show(this, Tr("Сочетания клавиш", "Keyboard shortcuts"), grid);
    }

    void About_Click(object s, RoutedEventArgs e) => DarkDialog.Show(this, "Sharp Agent", new StackPanel
    {
        Children =
        {
            new TextBlock { Text = Tr($"Версия {App.Version}", $"Version {App.Version}"), Margin = new(0, 0, 0, 8) },
            new TextBlock
            {
                Text = Tr($"ИИ-агент для работы с файлами и папками на этом компьютере. Работает от вашего имени, без прав администратора. Провайдер выбирается в настройках. Всё своё хранит в папке {FolderKind}.",
                    $"An AI agent for working with files on this computer. It runs as your user without administrator rights. Choose a provider in settings. The app stores its files in the {FolderKind} folder."),
                TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16),
            }.Paint(TextBlock.ForegroundProperty, "TextSoft"),
            new TextBlock { Text = Tr($"Папка {FolderKind}", $"{FolderKind} folder"), FontSize = 13, Margin = new(0, 0, 0, 8) }.Paint(TextBlock.ForegroundProperty, "TextDim"),
            _links.FileChip(AppPaths.AppFolder, AppPaths.AppFolder),
        },
    });
}
