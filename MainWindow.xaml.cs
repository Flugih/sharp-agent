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

    static readonly Dictionary<string, (string Label, string Hint)> EffortNames = new()
    {
        ["minimal"] = ("Минимальное", "почти без рассуждений"),
        ["low"] = ("Низкое", "быстрее и дешевле"),
        ["medium"] = ("Среднее", "баланс скорости и тщательности"),
        ["high"] = ("Высокое", "тщательнее, но дольше и дороже"),
        ["xhigh"] = ("Очень высокое", "для сложных задач, ещё дольше"),
        ["max"] = ("Максимальное", "почти предельная тщательность"),
        ["ultra"] = ("Ультра", "предельная тщательность, дольше всего"),
    };

    readonly ToolContext _toolContext = new()
    {
        AppFolder = AppPaths.AppFolder,
        InstallationFolder = AppPaths.IsInstalled ? Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) : null,
        PluginsFolder = AppPaths.PluginsFolder,
        LogsFolder = AppPaths.LogsFolder,
    };
    readonly UiState _state = UiState.Load();
    readonly ChatLinks _links;
    readonly MarkdownRenderer _markdown;
    readonly UsageTracker _usage;
    readonly ChatGptAuth _chatGpt = new(AppPaths.ChatGptAuthPath);
    readonly Dictionary<AgentProvider, List<string>> _modelLists = [];
    readonly DispatcherTimer _subagentTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    Dictionary<string, ModelInfo> _models = new(StringComparer.OrdinalIgnoreCase);
    List<string>? _imageModels;
    AppConfig _config = null!;
    AgentRunner _agent = null!;
    ChatApiClient? _tooken;
    IChatProvider? _provider;
    CancellationTokenSource? _cts;
    Task? _run;
    bool _closeConfirmed, _waitingToClose, _confirmOff;

    public MainWindow()
    {
        ApplyTheme();
        InitializeComponent();
        _links = new(this, new PathRefs(() => _toolContext.WorkingDirectory), OpenInApp);
        _markdown = new(_links.Markdown);
        _usage = new(_state);
        ChatScroll.PreviewMouseWheel += ChatScroll_PreviewMouseWheel;
        Action<string, bool> notice = (text, error) => ShowToast(text, error ? ToastKind.Error : ToastKind.Info);
        FileActions.Notify += notice;
        Closed += (_, _) =>
        {
            ResetBrowserNetwork(closing: true);
            FileActions.Notify -= notice;
            var compact = _compactChat;
            _compactChat = null;
            compact?.Close();
        };
        _subagentTimer.Tick += (_, _) => UpdateSubagentTimes();
        _voiceTimer.Tick += (_, _) => OnVoiceTick();
        RestoreWindowPlacement();
    }

    // The installed build keeps its data in %LOCALAPPDATA%, not next to the program.
    static string FolderKind => AppPaths.IsInstalled ? "данных" : "программы";

    static Visibility VisibleIf(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    static bool IsShown(UIElement element) => element.Visibility == Visibility.Visible;

    static void SetHint(FrameworkElement element, string hint)
    {
        element.ToolTip = hint;
        AutomationProperties.SetName(element, hint);
    }

    void Window_Loaded(object s, RoutedEventArgs e)
    {
        if (_agent != null) return;
        _config = AppConfig.Load();
        _agent = new(new ToolRegistry(_toolContext), new ChatStore(AppPaths.HistoryFolder, AppPaths.LogsFolder))
        {
            ConfirmAsync = ConfirmAsync, ClarifyAsync = AskClarificationAsync, CanSpendAsync = CheckLimitsAsync,
            ModelSupportsVision = id => _models.GetValueOrDefault(id)?.Vision, ModelContextTokens = id => _models.GetValueOrDefault(id)?.ContextTokens,
        };
        _agent.AssistantText += OnAssistantText;
        _agent.AssistantDelta += OnAssistantDelta;
        _agent.ProgressReported += OnModelProgress;
        _agent.ToolCallStarted += OnToolCallStarted;
        _agent.ToolCallFinished += OnToolCallFinished;
        _agent.ThinkingStarted += ShowThinking;
        _agent.SubagentChanged += OnSubagentChanged;
        _agent.UsageReported += (model, usage) =>
        {
            if (_config.Provider == AgentProvider.Tooken) _usage.AddLocal(model, usage.PromptTokens + usage.CompletionTokens);
            UpdateBalanceIndicator();
            UpdateContextBanner();
        };
        _agent.Warning += text => AddNotice(text, true);
        _agent.Stopped += text => AddNotice(text, true, false);
        _agent.Notice += text => AddNotice(text);
        _agent.Retrying += (attempt, total) => SetProgressHint($"Сервер не ответил, попытка {attempt} из {total}…");
        _agent.ChatCreated += () =>
        {
            _state.CurrentChat = Path.GetFileName(_agent.ChatPath);
            _state.Save();
            SyncCurrentChat(true);
        };
        ApplyConfig();
        UpdateConfirmToggle();
        ApplySidebar();
        _ = OpenStartupChatAsync();
        UpdateTrashCount();
        _ = LoadChatsAsync();
        _ = Task.Run(() => CommandLog.Prune(AppPaths.LogsFolder));
        if (_config.LoadError != null)
            FolderBanner("env", "Не удалось прочитать файл настроек .env", "Пока действуют настройки по умолчанию. Причина: " + _config.LoadError);
        if (!AppPaths.IsWritable(AppPaths.AppFolder))
            FolderBanner("readonly", $"Папка {FolderKind} недоступна для записи", "Настройки и переписка не сохранятся. " +
                (AppPaths.IsInstalled ? "Проверьте права доступа к этой папке." : "Перенесите папку Sharp Agent, например, на Рабочий стол."));
        if (!string.IsNullOrEmpty(_state.Draft))
        {
            InputBox.Text = _state.Draft;
            InputBox.CaretIndex = InputBox.Text.Length;
        }
        UpdateSendButton();
        if (_provider == null) Dispatcher.InvokeAsync(ShowSettings, DispatcherPriority.Loaded);
        else InputBox.Focus();
        void FolderBanner(string key, string title, string text) =>
            ShowBanner(key, Glyphs.Warning, title, text, "Открыть папку", () => FileActions.OpenFolder(AppPaths.AppFolder), true);
    }

    async void Window_Closing(object? s, CancelEventArgs e)
    {
        if (_waitingToClose)
        {
            e.Cancel = true;
            return;
        }
        var run = !_closeConfirmed && _run is { IsCompleted: false } active ? active : null;
        if (run != null || _chatOperation is { IsCompleted: false })
        {
            e.Cancel = true;
            _waitingToClose = true;
            try
            {
                if (run != null)
                {
                    if (!DarkDialog.Ask(ActiveChatWindow, "Агент ещё работает",
                            "Если закрыть программу сейчас, текущая задача прервётся на середине. Переписка сохранится.",
                            "Остановить и закрыть", "Не закрывать", true))
                        return;
                    _closeConfirmed = true;
                    SaveState();
                    StopAgent();
                    await Task.WhenAny(run, Task.Delay(5000));
                    await _agent.WaitForChatWritesAsync();
                }
                if (_chatOperation is { } operation) await operation;
                _ = Dispatcher.InvokeAsync(Close);
            }
            finally { _waitingToClose = false; }
            return;
        }
        _recorder?.Cancel();
        CloseOfficePreview();
        SaveState();
    }

    void SaveState()
    {
        _state.Window = CapturePlacement();
        _state.Draft = InputBox.Text.Length > 0 ? InputBox.Text : null;
        _state.CurrentChat = Path.GetFileName(_agent?.ChatPath);
        _state.Save();
    }

    void ApplyConfig()
    {
        bool routeChanged = _browserNetwork != _config.Network;
        if (routeChanged) ResetBrowserNetwork();
        AppNetwork.Apply(_config.Network);
        _browserNetwork = _config.Network;
        if (routeChanged && _browserTab != null && IsShown(BrowserPane)) SelectBrowserTab(_browserTab);
        _agent.MaxTokens = _config.MaxTokens;
        _toolContext.SandboxRoot = _config.Sandbox ? AppPaths.AppFolder : null;
        _toolContext.ComputerUse = _config.ComputerUse;
        ApplyWorkDir();
        ApplyProvider();
        ApplyFastMode();
        ApplyModeColors();
        ModeText.Text = _config.Sandbox ? "Песочница" : "Весь ПК";
        ModeIcon.Text = _config.Sandbox ? Glyphs.Lock : Glyphs.Globe;
        ModePill.ToolTip = (_config.Sandbox
            ? $"Агент работает только внутри папки {FolderKind}:\n{AppPaths.AppFolder}\nКоманды PowerShell и cmd отключены."
            : "Агент работает с любыми файлами пользователя, без прав администратора.") + "\nЩелчок — открыть настройки";
        AutomationProperties.SetName(ModePill, $"Режим: {ModeText.Text}. Открыть настройки");
    }

    void ApplyModeColors()
    {
        ModePill.Foreground = _config.Sandbox ? Theme.AccentText : Theme.TextMuted;
        ModePill.BorderBrush = _config.Sandbox ? Theme.AccentLine : Theme.LineStrong;
    }

    public void ApplyTheme()
    {
        if (!Theme.Apply(Application.Current.Resources, _state.Theme)) return;
        WindowEffects.ApplyFrame(this, ((SolidColorBrush)Theme.LineStrong).Color);
        if (_config == null) return;
        ApplyModeColors();
        Composer.BorderBrush = InputBox.IsKeyboardFocused ? Theme.Accent : Theme.LineStrong;
        RenderBanner();
        RenderPendingAttachments();
        UpdateBalanceIndicator();
    }

    string? SaveEnv(Dictionary<string, string?> values)
    {
        try
        {
            if (!File.Exists(AppPaths.EnvPath)) File.WriteAllText(AppPaths.EnvPath, AppConfig.EnvTemplate);
            DotEnv.Save(AppPaths.EnvPath, values);
            _config = AppConfig.Load();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }

    void ApplyWorkDir()
    {
        var folder = _toolContext.WorkingDirectory = _config.WorkingDirectory;
        var dir = Path.TrimEndingDirectorySeparator(folder);
        FolderText.Text = Path.GetFileName(dir) is { Length: > 0 } name ? name : dir;
        FolderButton.ToolTip = $"Рабочая папка агента:\n{folder}\nЩелчок — выбрать другую. Ctrl+Shift+O — открыть в Проводнике";
        AutomationProperties.SetName(FolderButton, $"Рабочая папка {FolderText.Text}. Выбрать другую");
    }

    void FolderButton_Click(object s, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            ShowToast("Папку можно сменить, когда агент закончит работу", ToastKind.Warning);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Рабочая папка агента" };
        if (Directory.Exists(_toolContext.WorkingDirectory)) dialog.InitialDirectory = _toolContext.WorkingDirectory;
        if (dialog.ShowDialog(this) != true) return;
        var folder = dialog.FolderName;
        bool inside = PathHelper.IsInside(folder, AppPaths.AppFolder);
        if (_config.Sandbox && !inside)
        {
            ShowToast($"В песочнице агенту доступна только папка {FolderKind}. Выберите папку внутри неё или выключите песочницу в настройках.",
                ToastKind.Warning);
            return;
        }
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            ShowToast("Эта папка недоступна: " + ex.Message, ToastKind.Warning);
            return;
        }
        if (SaveEnv(new() { ["AGENT_WORKDIR"] = inside ? Path.GetRelativePath(AppPaths.AppFolder, folder) : folder }) is { } error)
        {
            ShowToast("Не удалось сохранить выбор в .env: " + error, ToastKind.Error);
            return;
        }
        ApplyWorkDir();
        ShowToast("Агент работает в папке " + _toolContext.WorkingDirectory, ToastKind.Success);
    }

    void OpenWorkDir_Click(object s, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_toolContext.WorkingDirectory); }
        catch (Exception ex)
        {
            ShowToast("Не удалось создать рабочую папку: " + ex.Message, ToastKind.Error);
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
        if (!DarkDialog.Ask(this, "Отключить подтверждения?",
                "Агент сможет удалять и перезаписывать файлы и выполнять команды, ни о чём не спрашивая. " +
                "Подтверждения снова включатся при следующем запуске программы.", "Отключить", danger: true))
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
        ConfirmLabel.Text = on ? "Подтверждения" : "Без подтверждений";
        ConfirmToggle.ToolTip = on
            ? "Агент спрашивает разрешение перед удалением, перезаписью файлов и запуском команд. Щелчок — отключить"
            : "Агент удаляет, перезаписывает файлы и запускает команды без вопросов. Щелчок — снова спрашивать";
        AutomationProperties.SetName(ConfirmToggle, on ? "Подтверждения включены" : "Подтверждения выключены");
        if (on)
        {
            ConfirmToggle.ClearValue(ForegroundProperty);
            ConfirmToggle.ClearValue(BorderBrushProperty);
        }
        else ConfirmToggle.Paint(ForegroundProperty, "Warning").Paint(BorderBrushProperty, "WarningLine");
    }

    Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        if (_confirmOff) return Task.FromResult(true);
        SetTaskbar(TaskbarItemProgressState.Paused);
        var owner = ActiveChatWindow;
        WindowEffects.Flash(owner);
        bool allowed = DarkDialog.Ask(owner, request.Title, request.Note ?? "", request.Action, "Отклонить", request.Danger, request.Subject, true);
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
        UpdateSendButton();
    }

    void UpdateInputHint() => InputHint.Visibility = VisibleIf(InputBox.Text.Length == 0 && IsShown(InputBox));

    void UpdateSendButton()
    {
        bool hasDraft = !string.IsNullOrWhiteSpace(InputBox.Text) || _pendingAttachments.Count > 0, resume = !hasDraft && _agent?.CanContinue == true;
        SendButton.IsEnabled = !ChatFileBusy && (hasDraft || resume);
        SendButton.Content = resume ? Glyphs.Play : Glyphs.Send;
        SendButton.ToolTip = _attachmentImports > 0 ? "Подготавливаю вложения…" : resume ? "Продолжить задачу (Enter)" : "Отправить (Enter). Новая строка — Shift+Enter";
        AutomationProperties.SetName(SendButton, resume ? "Продолжить задачу" : "Отправить");
        UpdateCompactAction();
    }

    void UpdateComposerButtons()
    {
        bool voice = VoiceShown, running = _cts != null;
        SendButton.Visibility = VisibleIf(!voice && !running);
        StopButton.Visibility = VisibleIf(running);
        StopButton.ToolTip = voice ? "Остановить агента" : "Остановить агента (Esc)";
        MicButton.Visibility = VisibleIf(!voice);
        UpdateSendButton();
    }

    void InputBox_FocusChanged(object s, KeyboardFocusChangedEventArgs e) =>
        Composer.BorderBrush = ReferenceEquals(e.NewFocus, InputBox) ? Theme.Accent : Theme.LineStrong;

    async Task SendAsync()
    {
        if (ChatFileBusy || _waitingToClose) return;
        if (_cts != null)
        {
            ShowToast("Агент ещё работает. Дождитесь ответа или остановите его (Esc)", ToastKind.Warning);
            return;
        }
        var text = InputBox.Text.Trim();
        var attachments = _pendingAttachments.ToList();
        if (text.Length == 0 && attachments.Count == 0)
        {
            await RetryAsync();
            return;
        }
        if ((attachments.Count == 0 && AgentRunner.LocalCommand(text) != null ? CurrentModel() : ModelForRequest()) is not { } model) return;
        InputBox.Clear();
        ClearPendingAttachments();
        ClearSubagents();
        ScrollToLatest();
        var sentAt = DateTimeOffset.UtcNow;
        AddUserBubble(text, attachments);
        await (_run = RunAgentAsync(ct => _agent.RunAsync(text, attachments, model, ct, sentAt)));
    }

    string? ModelForRequest()
    {
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
        if (ChatFileBusy || _waitingToClose || _cts != null || !_agent.CanContinue ||
            (_agent.PendingLocalCommand != null ? CurrentModel() : ModelForRequest()) is not { } model) return;
        RemoveErrorCard();
        HideChangeCards();
        if (_chatView.LastStopNotice != null) ChatPanel.Children.Remove(_chatView.LastStopNotice);
        _chatView.LastStopNotice = null;
        ScrollToLatest();
        await (_run = RunAgentAsync(ct => _agent.ContinueAsync(model, ct), continuation: true));
    }

    async Task RunAgentAsync(Func<CancellationToken, Task> run, bool continuation = false)
    {
        var cts = _cts = new CancellationTokenSource();
        bool completed = false;
        SetRunning(true);
        StartProgress();
        try
        {
            var task = run(cts.Token);
            SyncCompactChat();
            await task;
            completed = true;
            if (!ActiveChatWindow.IsActive) WindowEffects.CompletionSound();
            WindowEffects.Flash(ActiveChatWindow);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested || _limitDeclined)
        {
            await _agent.RecordStopAsync(cts.IsCancellationRequested ? "Агент остановлен." : "Агент остановлен: исчерпан ваш лимит расхода.");
        }
        catch (OperationCanceledException)
        {
            AddError("Сервер не ответил за отведённое время. Проверьте подключение к интернету и повторите.", ErrorFix.Retry);
        }
        catch (ApiException ex)
        {
            if (_provider is ChatGptClient) App.LogError(ex);
            AddError(ex.Message, FixFor(ex));
        }
        catch (HttpRequestException ex)
        {
            AddError("Нет связи с сервером. Проверьте подключение к интернету и адрес API в настройках.\n" + ex.Message, ErrorFix.Retry);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AddError("Не удалось выполнить запрос: " + ex.Message, ErrorFix.Retry);
        }
        finally
        {
            cts.Dispose();
            _cts = null;
            _limitDeclined = false;
            ClearDraft(interrupted: true);
            MarkUnfinishedToolRows();
            FinishProgress("Выполнение прервано");
            if (!continuation || completed) FlushTurnFiles();
            SyncCompactChat();
            SetRunning(false);
            SyncCurrentChat(true);
            if (_compactChat == null && (!IsKeyboardFocusWithin || Keyboard.FocusedElement is not TextBoxBase)) ComposerFocus.Focus();
            if (_provider is not ChatGptClient)
            {
                _ = RefreshUsageAsync();
                _ = _chatGpt.RenewAsync();
            }
            else if (!_chatGpt.PlanAllowed) ApplyProvider();
        }
    }

    void SetRunning(bool running)
    {
        UpdateComposerButtons();
        StopButton.IsEnabled = true;
        ModelButton.IsEnabled = !running;
        _subagentTimer.IsEnabled = running;
        if (running) SetTaskbar(TaskbarItemProgressState.Indeterminate);
        else if (Taskbar.ProgressState != TaskbarItemProgressState.Error) SetTaskbar(TaskbarItemProgressState.None);
    }

    void SetTaskbar(TaskbarItemProgressState state)
    {
        Taskbar.ProgressState = state;
        Taskbar.ProgressValue = state is TaskbarItemProgressState.Paused or TaskbarItemProgressState.Error ? 1 : 0;
    }

    void Window_PreviewKeyDown(object s, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            if (Keyboard.FocusedElement is TextBox box && ChatList.IsAncestorOf(box)) return;
            if (IsShown(PictureOverlay)) ClosePicture();
            else if (SettingsOpen) CloseSettings();
            else if (IsShown(FlyoutLayer)) CloseFlyout();
            else if (_overlayOpen) CloseOverlay();
            else if (VoiceShown) CancelVoice();
            else if (IsShown(SubagentsWorkspace)) CloseSubagents();
            else if (IsShown(BrowserPane) && (_browserExpanded || BrowserPane.IsKeyboardFocusWithin)) HideBrowser();
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
            ("Enter", "отправить сообщение"), ("Shift+Enter", "новая строка"),
            ("Esc", "отменить голосовой ввод, остановить агента, закрыть панель"),
            ("Ctrl+N", "новый чат"), ("Ctrl+H", "показать или скрыть список чатов"), ("F2, Delete", "переименовать, удалить чат в списке"),
            ("Ctrl+O", "прикрепить файлы"), ("Ctrl+V", "вставить файлы или скриншот из буфера"),
            ("Ctrl+Shift+O", "открыть рабочую папку"), ("Ctrl+Shift+C", "скопировать последний ответ"), ("Ctrl+B", "баланс и расход"),
            ("Ctrl+,", "настройки"), ("Ctrl+L", "перейти в поле ввода"), ("PageUp, PageDown", "листать переписку"), ("F10", "другие команды"),
        ];
        var grid = new Grid { ColumnDefinitions = { new() { Width = GridLength.Auto }, new() } };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var keys = new TextBlock { Text = rows[i].Keys, FontFamily = Theme.Mono, FontSize = 13, Foreground = Theme.Text, Margin = new(0, 0, 24, 8) };
            var action = new TextBlock { Text = rows[i].Action, FontSize = 13, Foreground = Theme.TextSoft, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }
        DarkDialog.Show(this, "Сочетания клавиш", grid, 480);
    }

    void About_Click(object s, RoutedEventArgs e) => DarkDialog.Show(this, "Sharp Agent", new StackPanel
    {
        Children =
        {
            new TextBlock { Text = $"Версия {App.Version}", FontSize = 14, Foreground = Theme.Text, Margin = new(0, 0, 0, 8) },
            new TextBlock
            {
                Text = "ИИ-агент для работы с файлами и папками на этом компьютере. Работает от вашего имени, " +
                       $"без прав администратора. Провайдер выбирается в настройках. Всё своё хранит в папке {FolderKind}.",
                FontSize = 13, Foreground = Theme.TextSoft, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16),
            },
            new TextBlock { Text = $"Папка {FolderKind}", FontSize = 12, Foreground = Theme.TextMuted, Margin = new(0, 0, 0, 6) },
            _links.FileChip(AppPaths.AppFolder, AppPaths.AppFolder),
        },
    }, 480);
}
