using System.ComponentModel;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using SharpAgent.Agent;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Tools;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow : Window
{
    private static readonly string[] DefaultModels =
    [
        "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5", "claude-fable-5-2", "claude-opus-4-5",
        "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6-luna", "gpt-6-astra",
        "deepseek-v4-pro", "deepseek-v4.1-flash-1017", "kimi-k3.1-pro", "glm-5.3", "glm-5.3-flash",
        "gemini-4-argon", "gemini-3.8-flash",
    ];

    private static readonly string[] ChatGptModels =
    [
        "gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-6.1-terra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5",
    ];

    private static readonly string[] GroqModels = ["openai/gpt-oss-120b", "openai/gpt-oss-20b", "qwen/qwen3.8-27b"];

    private static readonly Dictionary<string, (string Label, string Hint)> EffortNames = new()
    {
        ["minimal"] = ("Минимальное", "почти без рассуждений"),
        ["low"] = ("Низкое", "быстрее и дешевле"),
        ["medium"] = ("Среднее", "баланс скорости и тщательности"),
        ["high"] = ("Высокое", "тщательнее, но дольше и дороже"),
        ["xhigh"] = ("Очень высокое", "для сложных задач, ещё дольше"),
        ["max"] = ("Максимальное", "почти предельная тщательность"),
        ["ultra"] = ("Ультра", "предельная тщательность, дольше всего"),
    };

    private readonly ToolContext _toolContext = new() { AppFolder = AppPaths.AppFolder, LogsFolder = AppPaths.LogsFolder };
    private readonly UiState _state = UiState.Load();
    private readonly ChatLinks _links;
    private readonly MarkdownRenderer _markdown;
    private readonly UsageTracker _usage;
    private readonly ChatGptAuth _chatGpt = new(AppPaths.ChatGptAuthPath);
    private readonly Dictionary<AgentProvider, List<string>> _modelLists = new();
    private readonly DispatcherTimer _subagentTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Dictionary<string, ModelInfo> _models = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _imageModels;
    private AppConfig _config = null!;
    private AgentRunner _agent = null!;
    private ChatApiClient? _tooken;
    private IChatProvider? _provider;
    private CancellationTokenSource? _cts;
    private Task? _run;
    private bool _closeConfirmed;
    private bool _confirmOff;

    public MainWindow()
    {
        InitializeComponent();
        _links = new ChatLinks(this, new PathRefs(() => _toolContext.WorkingDirectory), OpenInApp);
        _markdown = new MarkdownRenderer(_links.Markdown);
        _usage = new UsageTracker(_state);

        ChatScroll.PreviewMouseWheel += ChatScroll_PreviewMouseWheel;
        FileActions.Notify += OnFileNotice;
        Closed += (_, _) => FileActions.Notify -= OnFileNotice;
        _subagentTimer.Tick += (_, _) => UpdateSubagentTimes();
        _voiceTimer.Tick += (_, _) => OnVoiceTick();
        RestoreWindowPlacement();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _config = AppConfig.Load();
        _agent = new AgentRunner(new ToolRegistry(_toolContext), new ChatStore(AppPaths.HistoryFolder, AppPaths.LogsFolder))
        {
            ConfirmAsync = ConfirmAsync,
            ClarifyAsync = AskClarificationAsync,
            CanSpendAsync = CheckLimitsAsync,
            ModelSupportsVision = id => _models.TryGetValue(id, out var info) ? info.Vision : null,
            ModelContextTokens = id => _models.TryGetValue(id, out var info) ? info.ContextTokens : null,
        };
        _agent.AssistantText += OnAssistantText;
        _agent.AssistantDelta += OnAssistantDelta;
        _agent.ProgressReported += OnModelProgress;
        _agent.ToolCallStarted += OnToolCallStarted;
        _agent.ToolCallFinished += OnToolCallFinished;
        _agent.ThinkingStarted += ShowThinking;
        _agent.SubagentChanged += OnSubagentChanged;
        _agent.UsageReported += OnUsage;
        _agent.Warning += text => AddNotice(text, warning: true);
        _agent.Stopped += text => AddNotice(text, warning: true, inProgress: false);
        _agent.Notice += text => AddNotice(text);
        _agent.Retrying += (attempt, total) => SetProgressHint($"Сервер не ответил, попытка {attempt} из {total}…");
        _agent.ChatCreated += OnChatCreated;

        ApplyConfig();
        UpdateConfirmToggle();
        ApplySidebar();
        OpenStartupChat();
        _ = LoadChatsAsync();
        _ = Task.Run(() => CommandLog.Prune(AppPaths.LogsFolder));

        if (_config.LoadError != null)
            ShowBanner("env", Glyphs.Warning, "Не удалось прочитать файл настроек .env",
                "Пока действуют настройки по умолчанию. Причина: " + _config.LoadError,
                "Открыть папку", () => FileActions.OpenFolder(AppPaths.AppFolder), warning: true);
        if (!AppPaths.IsWritable(AppPaths.AppFolder))
            ShowBanner("readonly", Glyphs.Warning, "Папка программы недоступна для записи",
                "Настройки и переписка не сохранятся. Перенесите папку Sharp Agent, например, на Рабочий стол.",
                "Открыть папку", () => FileActions.OpenFolder(AppPaths.AppFolder), warning: true);

        if (!string.IsNullOrEmpty(_state.Draft))
        {
            InputBox.Text = _state.Draft;
            InputBox.CaretIndex = InputBox.Text.Length;
        }
        UpdateSendButton();

        if (_provider == null) Dispatcher.InvokeAsync(ShowSettings, DispatcherPriority.Loaded);
        else InputBox.Focus();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_run is { IsCompleted: false } run && !_closeConfirmed)
        {
            e.Cancel = true;
            if (!DarkDialog.Ask(this, "Агент ещё работает",
                    "Если закрыть программу сейчас, текущая задача прервётся на середине. Переписка сохранится.",
                    "Остановить и закрыть", "Не закрывать", danger: true))
                return;
            _closeConfirmed = true;
            SaveState();
            StopAgent();
            await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
            _ = Dispatcher.InvokeAsync(Close);
            return;
        }
        _recorder?.Cancel();
        CloseOfficePreview();
        SaveState();
    }

    private void SaveState()
    {
        _state.Window = CapturePlacement();
        _state.Draft = InputBox.Text.Length > 0 ? InputBox.Text : null;
        _state.CurrentChat = _agent?.ChatPath is { } path ? Path.GetFileName(path) : null;
        _state.Save();
    }

    private void ApplyConfig()
    {
        _agent.MaxTokens = _config.MaxTokens;
        _agent.UseSubagents = _config.UseSubagents;
        _toolContext.SandboxRoot = _config.Sandbox ? AppPaths.AppFolder : null;
        ApplyWorkDir();
        ApplyProvider();

        ApplyModeColors();
        ModeText.Text = _config.Sandbox ? "Песочница" : "Весь ПК";
        ModeIcon.Text = _config.Sandbox ? Glyphs.Lock : Glyphs.Globe;
        ModePill.ToolTip = (_config.Sandbox
            ? $"Агент работает только внутри папки программы:\n{AppPaths.AppFolder}\nКоманды PowerShell и cmd отключены."
            : "Агент работает с любыми файлами пользователя, без прав администратора.") + "\nЩелчок — открыть настройки";
        AutomationProperties.SetName(ModePill, $"Режим: {ModeText.Text}. Открыть настройки");
    }

    private void ApplyModeColors()
    {
        var brush = _config.Sandbox ? Theme.AccentText : Theme.TextMuted;
        ModePill.Foreground = brush;
        ModePill.BorderBrush = _config.Sandbox ? Theme.AccentLine : Theme.LineStrong;
    }

    public void OnAccentChanged()
    {
        if (_config == null) return;
        ApplyModeColors();
        Composer.BorderBrush = InputBox.IsKeyboardFocused ? Theme.Accent : Theme.LineStrong;
        RenderBanner();
        RenderPendingAttachments();
        UpdateBalanceIndicator();
        if (_cts == null) RenderCurrentChat();
    }

    private string? SaveEnv(Dictionary<string, string?> values)
    {
        try
        {
            if (!File.Exists(AppPaths.EnvPath))
                File.WriteAllText(AppPaths.EnvPath, AppConfig.EnvTemplate, new System.Text.UTF8Encoding(false));
            DotEnv.Save(AppPaths.EnvPath, values);
            _config = AppConfig.Load();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private void ApplyWorkDir()
    {
        _toolContext.WorkingDirectory = _config.WorkingDirectory;
        var dir = Path.TrimEndingDirectorySeparator(_config.WorkingDirectory);
        FolderText.Text = Path.GetFileName(dir) is { Length: > 0 } name ? name : dir;
        FolderButton.ToolTip = $"Рабочая папка агента:\n{_config.WorkingDirectory}\n" +
                               "Щелчок — выбрать другую. Ctrl+Shift+O — открыть в Проводнике";
        AutomationProperties.SetName(FolderButton, $"Рабочая папка {FolderText.Text}. Выбрать другую");
    }

    private void FolderButton_Click(object sender, RoutedEventArgs e)
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
        if (WorkDirProblem(folder, _config.Sandbox) is { } problem)
        {
            ShowToast(problem, ToastKind.Warning);
            return;
        }
        var value = PathHelper.IsInside(folder, AppPaths.AppFolder) ? Path.GetRelativePath(AppPaths.AppFolder, folder) : folder;
        if (SaveEnv(new Dictionary<string, string?> { ["AGENT_WORKDIR"] = value }) is { } error)
        {
            ShowToast("Не удалось сохранить выбор в .env: " + error, ToastKind.Error);
            return;
        }
        ApplyWorkDir();
        ShowToast("Агент работает в папке " + _toolContext.WorkingDirectory, ToastKind.Success);
    }

    private static string? WorkDirProblem(string folder, bool sandbox)
    {
        if (sandbox && !PathHelper.IsInside(folder, AppPaths.AppFolder))
            return "В песочнице агенту доступна только папка программы. Выберите папку внутри неё или выключите песочницу в настройках.";
        try
        {
            Directory.CreateDirectory(folder);
            return null;
        }
        catch (Exception ex)
        {
            return "Эта папка недоступна: " + ex.Message;
        }
    }

    private void OpenWorkDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_toolContext.WorkingDirectory);
        }
        catch (Exception ex)
        {
            ShowToast("Не удалось создать рабочую папку: " + ex.Message, ToastKind.Error);
            return;
        }
        FileActions.OpenFolder(_toolContext.WorkingDirectory);
    }

    private void ModePill_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ConfirmToggle_Checked(object sender, RoutedEventArgs e)
    {
        _confirmOff = false;
        if (IsLoaded) UpdateConfirmToggle();
    }

    private void ConfirmToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        bool off = DarkDialog.Ask(this, "Отключить подтверждения?",
            "Агент сможет удалять и перезаписывать файлы и выполнять команды, ни о чём не спрашивая. " +
            "Подтверждения снова включатся при следующем запуске программы.",
            "Отключить", danger: true);
        if (!off)
        {
            ConfirmToggle.IsChecked = true;
            return;
        }
        _confirmOff = true;
        UpdateConfirmToggle();
    }

    private void UpdateConfirmToggle()
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
        else
        {
            ConfirmToggle.Foreground = Theme.Warning;
            ConfirmToggle.BorderBrush = Theme.WarningLine;
        }
    }

    private Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        if (_confirmOff) return Task.FromResult(true);
        SetTaskbar(TaskbarItemProgressState.Paused);
        WindowEffects.Flash(this);
        bool allowed = DarkDialog.Ask(this, request.Title, request.Note ?? "", request.Action, "Отклонить",
            request.Danger, request.Subject, delayed: true);
        SetTaskbar(TaskbarItemProgressState.Indeterminate);
        return Task.FromResult(allowed);
    }

    private void Send_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

    private void Stop_Click(object sender, RoutedEventArgs e) => StopAgent();

    private void StopAgent()
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        StopButton.IsEnabled = false;
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        _ = SendAsync();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateInputHint();
        UpdateSendButton();
    }

    private void UpdateInputHint() =>
        InputHint.Visibility = InputBox.Text.Length == 0 && InputBox.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void UpdateSendButton()
    {
        bool hasDraft = !string.IsNullOrWhiteSpace(InputBox.Text) || _pendingAttachments.Count > 0;
        bool resume = !hasDraft && _agent?.CanContinue == true;
        SendButton.IsEnabled = hasDraft || resume;
        SendButton.Content = resume ? Glyphs.Play : Glyphs.Send;
        SendButton.ToolTip = resume ? "Продолжить задачу (Enter)" : "Отправить (Enter). Новая строка — Shift+Enter";
        AutomationProperties.SetName(SendButton, resume ? "Продолжить задачу" : "Отправить");
    }

    private void UpdateComposerButtons()
    {
        bool voice = VoiceShown, running = _cts != null;
        SendButton.Visibility = voice || running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.ToolTip = voice ? "Остановить агента" : "Остановить агента (Esc)";
        MicButton.Visibility = voice ? Visibility.Collapsed : Visibility.Visible;
        UpdateSendButton();
    }

    private void InputBox_FocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        Composer.BorderBrush = ReferenceEquals(e.NewFocus, InputBox) ? Theme.Accent : Theme.LineStrong;

    private async Task SendAsync()
    {
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
        var model = attachments.Count == 0 && AgentRunner.LocalCommand(text) != null ? CurrentModel() : ModelForRequest();
        if (model == null) return;

        InputBox.Clear();
        ClearPendingAttachments();
        ClearSubagents();
        ScrollToLatest();
        AddUserBubble(text, attachments);
        await RunAgentAsync(ct => _agent.RunAsync(text, attachments, model, ct));
    }

    private string? ModelForRequest()
    {
        if (_provider == null)
        {
            ShowToast(NotReadyText(), ToastKind.Warning);
            ShowSettings();
            return null;
        }

        var model = CurrentModel();
        if (DrawsOnly(model))
        {
            ShowToast($"{model} рисует картинки, но не ведёт чат. Выберите текстовую модель — картинку агент нарисует сам.",
                ToastKind.Warning);
            return null;
        }
        return model;
    }

    private async Task RetryAsync()
    {
        if (_cts != null || !_agent.CanContinue) return;
        var model = _agent.PendingLocalCommand != null ? CurrentModel() : ModelForRequest();
        if (model == null) return;
        RemoveErrorCard();
        if (_chatView.LastStopNotice != null) ChatPanel.Children.Remove(_chatView.LastStopNotice);
        _chatView.LastStopNotice = null;
        ScrollToLatest();
        await RunAgentAsync(ct => _agent.ContinueAsync(model, ct));
    }

    private async Task RunAgentAsync(Func<CancellationToken, Task> run)
    {
        var cts = _cts = new CancellationTokenSource();
        SetRunning(true);
        StartProgress();
        var task = run(cts.Token);
        _run = task;
        try
        {
            await task;
            WindowEffects.Flash(this);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _agent.RecordStop("Агент остановлен.");
        }
        catch (OperationCanceledException)
        {
            if (_limitDeclined) _agent.RecordStop("Агент остановлен: исчерпан ваш лимит расхода.");
            else AddError("Сервер не ответил за отведённое время. Проверьте подключение к интернету и повторите.", ErrorFix.Retry);
        }
        catch (ApiException ex)
        {
            if (_provider is ChatGptClient) App.LogError(ex);
            AddError(ex.Message, FixFor(ex));
        }
        catch (HttpRequestException ex)
        {
            AddError("Нет связи с сервером. Проверьте подключение к интернету и адрес API в настройках.\n" + ex.Message,
                ErrorFix.Retry);
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
            FlushTurnFiles();
            SetRunning(false);
            SyncCurrentChat(touched: true);
            if (!IsKeyboardFocusWithin || Keyboard.FocusedElement is not TextBoxBase) ComposerFocus.Focus();
            if (_provider is ChatGptClient && !_chatGpt.PlanAllowed) ApplyProvider();
            else if (_provider is not ChatGptClient)
            {
                _ = RefreshUsageAsync();
                _ = _chatGpt.RenewAsync();
            }
        }
    }

    private void SetRunning(bool running)
    {
        UpdateComposerButtons();
        StopButton.IsEnabled = true;
        ModelButton.IsEnabled = !running;

        if (running)
        {
            _subagentTimer.Start();
            SetTaskbar(TaskbarItemProgressState.Indeterminate);
        }
        else
        {
            _subagentTimer.Stop();
            if (Taskbar.ProgressState != TaskbarItemProgressState.Error) SetTaskbar(TaskbarItemProgressState.None);
        }
    }

    private void SetTaskbar(TaskbarItemProgressState state)
    {
        Taskbar.ProgressState = state;
        Taskbar.ProgressValue = state is TaskbarItemProgressState.Paused or TaskbarItemProgressState.Error ? 1 : 0;
    }

    private void OnUsage(string model, UsageInfo usage)
    {
        if (_config.Provider == AgentProvider.Tooken) _usage.AddLocal(model, usage.PromptTokens + usage.CompletionTokens);
        UpdateBalanceIndicator();
        UpdateContextBanner();
    }

    private void OnFileNotice(string message, bool isError) => ShowToast(message, isError ? ToastKind.Error : ToastKind.Info);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (key == Key.Escape)
        {
            if (Keyboard.FocusedElement is TextBox box && ChatList.IsAncestorOf(box)) return;
            if (PictureOverlay.Visibility == Visibility.Visible) ClosePicture();
            else if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings();
            else if (FlyoutLayer.Visibility == Visibility.Visible) CloseFlyout();
            else if (_overlayOpen) CloseOverlay();
            else if (VoiceShown) CancelVoice();
            else if (SubagentsWorkspace.Visibility == Visibility.Visible) CloseSubagents();
            else if (QuestionHost.Visibility == Visibility.Visible) StopAgent();
            else if (PreviewPane.Visibility == Visibility.Visible) ClosePreview();
            else if (_cts != null) StopAgent();
            else return;
            e.Handled = true;
            return;
        }

        if (SettingsOverlay.Visibility == Visibility.Visible) return;

        bool handled = true;
        if (modifiers == ModifierKeys.Control && key == Key.N) StartNewChat();
        else if (modifiers == ModifierKeys.Control && key == Key.H) ToggleSidebar();
        else if (modifiers == ModifierKeys.Control && key == Key.O) Attach_Click(this, e);
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.O) OpenWorkDir_Click(this, e);
        else if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C) CopyLastAnswer_Click(this, e);
        else if (modifiers == ModifierKeys.Control && key == Key.B) Balance_Click(this, e);
        else if (modifiers == ModifierKeys.Control && key == Key.OemComma) ShowSettings();
        else if (modifiers == ModifierKeys.Control && key == Key.L) InputBox.Focus();
        else if (modifiers == ModifierKeys.None && key == Key.F1) Shortcuts_Click(this, e);
        else if (modifiers == ModifierKeys.None && key == Key.F10) More_Click(this, e);
        else if (modifiers == ModifierKeys.None && key is Key.PageUp or Key.PageDown && InputBox.LineCount <= 1 &&
                 SubagentsWorkspace.Visibility != Visibility.Visible &&
                 (ReferenceEquals(Keyboard.FocusedElement, InputBox) || Keyboard.FocusedElement is not TextBoxBase))
            ScrollChatBy((key == Key.PageDown ? 1 : -1) * ChatScroll.ViewportHeight * 0.85);
        else handled = false;

        if (handled) e.Handled = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.IsOpen = true;
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        (string Keys, string Action)[] rows =
        [
            ("Enter", "отправить сообщение"),
            ("Shift+Enter", "новая строка"),
            ("Esc", "отменить голосовой ввод, остановить агента, закрыть панель"),
            ("Ctrl+N", "новый чат"),
            ("Ctrl+H", "показать или скрыть список чатов"),
            ("F2, Delete", "переименовать, удалить чат в списке"),
            ("Ctrl+O", "прикрепить файлы"),
            ("Ctrl+V", "вставить файлы или снимок экрана из буфера"),
            ("Ctrl+Shift+O", "открыть рабочую папку"),
            ("Ctrl+Shift+C", "скопировать последний ответ"),
            ("Ctrl+B", "баланс и расход"),
            ("Ctrl+,", "настройки"),
            ("Ctrl+L", "перейти в поле ввода"),
            ("PageUp, PageDown", "листать переписку"),
            ("F10", "другие команды"),
        ];

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock
            {
                Text = rows[i].Keys, FontFamily = Theme.Mono, FontSize = 13, Foreground = Theme.Text,
                Margin = new Thickness(0, 0, 24, 8),
            };
            var action = new TextBlock
            {
                Text = rows[i].Action, FontSize = 13, Foreground = Theme.TextSoft, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }
        DarkDialog.Show(this, "Сочетания клавиш", grid, 480);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"Версия {App.Version}", FontSize = 14, Foreground = Theme.Text, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "ИИ-агент для работы с файлами и папками на этом компьютере. Работает от вашего имени, " +
                   "без прав администратора, через Tooken Club, ChatGPT или Groq. Всё своё хранит в папке программы.",
            FontSize = 13, Foreground = Theme.TextSoft, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Папка программы", FontSize = 12, Foreground = Theme.TextMuted, Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(_links.FileChip(AppPaths.AppFolder, AppPaths.AppFolder));
        DarkDialog.Show(this, "Sharp Agent", panel, 480);
    }
}
