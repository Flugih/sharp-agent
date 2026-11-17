using System.Windows.Documents;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    private enum ToastKind { Info, Success, Warning, Error }

    private sealed record BannerInfo(string Glyph, string Title, string Text, string ActionText, Action Action, bool Warning);

    private readonly Dictionary<string, BannerInfo> _banners = new();
    private readonly Dictionary<AgentProvider, string> _settingsKeys = new(), _settingsModels = new(), _savedModels = new();
    private AgentProvider _settingsProvider;
    private CancellationTokenSource? _signIn;
    private DispatcherTimer? _toastTimer;
    private Action? _toastAction;
    private int _toastVersion;


    private static Hyperlink MakeLink(string text, Action click, string? tip = null)
    {
        var link = new Hyperlink(new Run(text)) { ToolTip = tip };
        link.Click += (_, _) => click();
        return link;
    }

    private void ShowToast(string text, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
    {
        _compactChat?.SetStatus(text.ReplaceLineEndings(" "));
        _toastVersion++;
        ToastText.Text = text;
        (ToastIcon.Text, ToastIcon.Foreground) = kind switch
        {
            ToastKind.Success => (Glyphs.Check, Theme.Success),
            ToastKind.Warning => (Glyphs.Warning, Theme.Warning),
            ToastKind.Error => (Glyphs.Warning, Theme.DangerText),
            _ => (Glyphs.Info, Theme.AccentText),
        };
        _toastAction = action;
        ToastAction.Content = actionText;
        ToastAction.Visibility = VisibleIf(action != null && actionText != null);
        ToastHost.IsHitTestVisible = action != null;
        AutomationProperties.SetLiveSetting(ToastText, kind == ToastKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        bool wasHidden = !IsShown(ToastHost), animate = Theme.AnimationsEnabled;
        ToastHost.Visibility = Visibility.Visible;
        ToastHost.BeginAnimation(OpacityProperty, !animate ? null
            : wasHidden ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) : new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
        if (!animate) ToastHost.Opacity = 1;
        if (_toastTimer == null) (_toastTimer = new()).Tick += (_, _) => HideToast();
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(2 + text.Length / 15.0, 3, 12) + (action != null ? 4 : 0));
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer?.Stop();
        _toastAction = null;
        if (!Theme.AnimationsEnabled)
        {
            ToastHost.Visibility = Visibility.Collapsed;
            return;
        }
        int version = _toastVersion;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (version == _toastVersion) ToastHost.Visibility = Visibility.Collapsed; };
        ToastHost.BeginAnimation(OpacityProperty, fade);
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        HideToast();
        action?.Invoke();
    }

    private void ShowBanner(string key, string glyph, string title, string text, string actionText, Action action, bool warning = false)
    {
        _banners[key] = new(glyph, title, text, actionText, action, warning);
        RenderBanner();
    }

    private void HideBanner(string key)
    {
        if (_banners.Remove(key)) RenderBanner();
    }

    private BannerInfo? TopBanner => _banners.Values.OrderByDescending(b => b.Warning).FirstOrDefault();

    private void RenderBanner()
    {
        var banner = TopBanner;
        Banner.Visibility = VisibleIf(banner != null);
        if (banner == null) return;
        BannerIcon.Text = banner.Glyph;
        BannerTitle.Text = banner.Title;
        BannerText.Text = banner.Text;
        BannerAction.Content = banner.ActionText;
        (BannerIcon.Foreground, Banner.Background, Banner.BorderBrush) = banner.Warning
            ? (Theme.Warning, Theme.WarningSoft, Theme.WarningLine)
            : (Theme.AccentText, Theme.Get("BannerBg"), Theme.AccentLine);
    }

    private void BannerAction_Click(object sender, RoutedEventArgs e) => TopBanner?.Action();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowSettings() => OpenSettings(null);

    private void OpenSettings(Control? focus)
    {
        if (_cts != null)
        {
            ShowToast("Остановите агента, чтобы изменить настройки", ToastKind.Warning);
            return;
        }
        if (IsShown(SettingsOverlay)) return;
        CloseFlyout();
        CloseOverlay();
        SettingsError.Visibility = Visibility.Collapsed;
        var env = new Dictionary<string, string>();
        try { if (File.Exists(AppPaths.EnvPath)) env = DotEnv.Parse(AppPaths.EnvPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowSettingsError("Не удалось прочитать .env: " + ex.Message);
            env = new()
            {
                ["TOOKEN_BASE_URL"] = _config.BaseUrl,
                ["TOOKEN_IMAGE_MODEL"] = _config.ImageModel ?? "", ["TOOKEN_MAX_TOKENS"] = $"{_config.MaxTokens}",
            };
        }
        string Value(string key, string fallback = "") => env.GetValueOrDefault(key) is { Length: > 0 } value ? value : fallback;
        foreach (var provider in Enum.GetValues<AgentProvider>())
        {
            if (provider != AgentProvider.ChatGpt)
                _settingsKeys[provider] = AppConfig.CleanKey(Value(AppConfig.KeyOf(provider, "API_KEY"), _config.ApiKeyFor(provider) ?? ""));
            _settingsModels[provider] = _savedModels[provider] =
                provider == _config.Provider ? CurrentModel() : Value(AppConfig.KeyOf(provider, "MODEL"), AppConfig.DefaultModelOf(provider));
        }
        ImageModelButton.Content = Value("TOOKEN_IMAGE_MODEL", AppConfig.DefaultImageModel);
        ImageModelButton.Tag = _imageModels ?? [(string)ImageModelButton.Content];
        SubagentsToggle.IsChecked = _config.UseSubagents;
        ComputerUseToggle.IsChecked = _config.ComputerUse;
        ThemeChoices.Children.OfType<RadioButton>().First(button => Equals(button.Tag, _state.Theme ?? "dark")).IsChecked = true;
        SubagentTaskLimitBox.Text = _config.SubagentTaskLimit.ToString(CultureInfo.InvariantCulture);
        SubagentStepLimitBox.Text = _config.SubagentStepLimit.ToString(CultureInfo.InvariantCulture);
        BaseUrlBox.Text = Value("TOOKEN_BASE_URL", AppConfig.DefaultBaseUrl);
        var plugins = PluginTools.Load(_toolContext.PluginsFolder);
        PluginsText.Text = plugins.Count > 0
            ? $"Установлены: {string.Join(", ", plugins.Select(p => p.Name))}. Агент сам выбирает подходящий плагин по задаче."
            : "Плагинов нет. Плагин — это папка с файлом PLUGIN.md внутри папки plugins.";
        MaxTokensBox.Text = Value("TOOKEN_MAX_TOKENS");
        DayLimitBox.Text = _config.DayLimit is { } day ? Tokens.ToMillions(day) : "";
        MonthLimitBox.Text = _config.MonthLimit is { } month ? Tokens.ToMillions(month) : "";
        BaseUrlBox.Tag = MaxTokensBox.Tag = DayLimitBox.Tag = MonthLimitBox.Tag = SubagentTaskLimitBox.Tag = SubagentStepLimitBox.Tag = null;
        _settingsProvider = _config.Provider;
        ProviderChoices.Children.OfType<RadioButton>().First(button => Equals(button.Tag, _settingsProvider)).IsChecked = true;
        ShowProviderSettings();
        SettingsOverlay.Visibility = Visibility.Visible;
        if (Theme.AnimationsEnabled) SettingsOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        Dispatcher.InvokeAsync(() =>
        {
            if (focus != null) focus.BringIntoView();
            else
            {
                SettingsScroll.ScrollToTop();
                focus = _settingsProvider == AgentProvider.ChatGpt ? (_chatGpt.PlanAllowed ? DefaultModelButton : ChatGptSignInButton)
                    : KeyPasswordBox.Password.Length == 0 ? KeyPasswordBox : DefaultModelButton;
            }
            focus.Focus();
        }, DispatcherPriority.Input);
    }

    private void ProviderPill_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: AgentProvider provider } || provider == _settingsProvider) return;
        KeepProviderInput();
        _settingsProvider = provider;
        ShowProviderSettings();
    }

    private void KeepProviderInput()
    {
        if (_settingsProvider != AgentProvider.ChatGpt) _settingsKeys[_settingsProvider] = EnteredKey();
        if (_settingsProvider != AgentProvider.Groq) _settingsKeys[AgentProvider.Groq] = AppConfig.CleanKey(VoiceKeyBox.Password);
        _settingsModels[_settingsProvider] = DefaultModelButton.Content as string ?? "";
    }

    private void ShowProviderSettings()
    {
        var provider = _settingsProvider;
        bool keyed = provider != AgentProvider.ChatGpt, groq = provider == AgentProvider.Groq;
        KeyPanel.Visibility = VisibleIf(keyed);
        ChatGptPanel.Visibility = VisibleIf(!keyed);
        TookenPanel.Visibility = VisibleIf(provider == AgentProvider.Tooken);
        if (keyed)
        {
            ShowKeyToggle.IsChecked = false;
            KeyTextBox.Text = KeyPasswordBox.Password = _settingsKeys.GetValueOrDefault(provider, "");
            KeyLabel.Text = "API-ключ " + ProviderName(provider);
            (KeyHint.Text, KeyLinkText.Text) = provider switch
            {
                AgentProvider.Groq => ("Ключ создаётся в консоли Groq и начинается с gsk_. ", "Открыть страницу ключей"),
                AgentProvider.Tooken => ("Ключ выдаётся в личном кабинете и начинается с tc_live_. ", "Открыть кабинет"),
                _ => ("Создайте API-ключ в кабинете провайдера. ", "Открыть страницу ключей"),
            };
            AutomationProperties.SetName(KeyPasswordBox, KeyLabel.Text);
            AutomationProperties.SetName(KeyTextBox, KeyLabel.Text);
        }
        else ShowChatGptAccount();
        VoiceKeyPanel.Visibility = VisibleIf(!groq);
        if (!groq) VoiceKeyBox.Password = _settingsKeys.GetValueOrDefault(AgentProvider.Groq, "");
        VoiceHint.Text = groq
            ? "Микрофон в поле ввода отправляет запись в Groq по ключу из раздела «Подключение» и вставляет распознанный текст. Запись нигде не сохраняется. "
            : "Микрофон в поле ввода отправляет запись в Groq и вставляет распознанный текст — при любом провайдере. Запись нигде не сохраняется. " +
              "Бесплатный ключ создаётся в консоли Groq и начинается с gsk_. ";
        DefaultModelButton.Content = _settingsModels.GetValueOrDefault(provider) is { Length: > 0 } model ? model : AppConfig.DefaultModelOf(provider);
        DefaultModelButton.Tag = _modelLists.GetValueOrDefault(provider) ?? (IEnumerable<string>)DefaultModelsOf(provider);
        SetTestResult("", null);
    }

    private void ShowChatGptAccount()
    {
        bool connected = _chatGpt.Connected, allowed = _chatGpt.PlanAllowed, waiting = _signIn != null;
        ChatGptAccountPanel.Visibility = VisibleIf(connected);
        ChatGptSignInPanel.Visibility = VisibleIf(!allowed);
        ChatGptEmail.Text = _chatGpt.Label ?? "Аккаунт ChatGPT";
        ChatGptPlan.Text = allowed ? "Подписка ChatGPT подключена" : "Использование подписки не разрешено";
        ChatGptSignInButton.IsEnabled = !waiting;
        var action = waiting ? "Ожидаю вход в браузере…" : connected ? "Разрешить использование подписки" : "Продолжить с ChatGPT";
        ChatGptSignInButton.Content = action;
        ChatGptSignInButton.ToolTip = connected ? "Открыть сайт OpenAI и разрешить Sharp Agent использовать подписку ChatGPT"
            : "Открыть сайт OpenAI, войти в ChatGPT и подключить подписку к Sharp Agent";
        AutomationProperties.SetName(ChatGptSignInButton, action);
        ChatGptCancelButton.Visibility = VisibleIf(waiting);
        ChatGptHint.Text = waiting ? "В браузере открылась страница OpenAI. Войдите и разрешите Sharp Agent использовать подписку — программа ждёт до 10 минут."
            : allowed ? "Запросы агента расходуют вашу подписку ChatGPT."
            : connected ? "Разрешите Sharp Agent использовать подписку на сайте OpenAI или выберите другого провайдера."
            : _chatGpt.Label is { } label ? $"Sharp Agent подключён к {label}: нажмите «Продолжить с ChatGPT», чтобы войти снова."
            : "Откроется сайт OpenAI. Войдите в ChatGPT и разрешите Sharp Agent использовать подписку.";
        if (waiting || connected) return;
        foreach (var account in _chatGpt.SavedAccounts)
            AddSignInLink("Войти как " + (account.Label ?? "аккаунт ChatGPT"), "Войти в сохранённый аккаунт ChatGPT", account.ClientId);
        if (_chatGpt.ClientId != null) AddSignInLink("Войти в другой аккаунт", "Подключить к Sharp Agent другой аккаунт ChatGPT", null);
    }

    private void AddSignInLink(string text, string tip, string? clientId)
    {
        ChatGptHint.Inlines.Add(new LineBreak());
        ChatGptHint.Inlines.Add(MakeLink(text, async () => await SignInChatGptAsync(clientId), tip));
    }

    private async void ChatGptSignIn_Click(object sender, RoutedEventArgs e) => await SignInChatGptAsync(_chatGpt.ClientId);

    private async Task SignInChatGptAsync(string? clientId)
    {
        if (_signIn != null) return;
        var cts = _signIn = new CancellationTokenSource();
        ShowChatGptAccount();
        SetTestResult("", null);
        bool welcome = false;
        try
        {
            welcome = await _chatGpt.SignInAsync(OpenSignInPage, clientId, cts.Token);
            WindowEffects.Flash(this);
            if (_config.Provider == AgentProvider.ChatGpt) ApplyProvider();
            else if (_chatGpt.PlanAllowed && _cts == null) SwitchProvider(AgentProvider.ChatGpt);
            bool allowed = _chatGpt.PlanAllowed;
            if (!IsShown(SettingsOverlay))
                ShowToast(allowed ? "Подписка ChatGPT подключена" + (_chatGpt.Label is { } label ? ": " + label : "")
                    : "Вход выполнен, но Sharp Agent не получил разрешения использовать подписку ChatGPT", allowed ? ToastKind.Success : ToastKind.Warning);
            else if (_settingsProvider == AgentProvider.ChatGpt && allowed) TestKey_Click(this, new RoutedEventArgs());
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ApiException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            var text = ex is ApiException ? ex.Message : "Не удалось войти в ChatGPT: " + ex.Message + " Попробуйте ещё раз.";
            if (IsShown(SettingsOverlay)) SetTestResult(text, false);
            else ShowToast(text, ToastKind.Error);
        }
        finally
        {
            cts.Dispose();
            _signIn = null;
            ShowChatGptAccount();
        }
        if (welcome) ShowPlanWelcome();
    }

    private static void OpenSignInPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new ApiException("Не удалось открыть браузер для входа в ChatGPT. Выберите браузер по умолчанию в параметрах Windows и нажмите «Продолжить с ChatGPT» ещё раз.");
        }
    }

    private void ChatGptCancel_Click(object sender, RoutedEventArgs e) => _signIn?.Cancel();

    private async void ChatGptSignOut_Click(object sender, RoutedEventArgs e)
    {
        if (!DarkDialog.Ask(this, "Выйти из ChatGPT?", "Чтобы снова работать через ChatGPT, нажмите «Продолжить с ChatGPT».", "Выйти")) return;
        ChatGptSignOutButton.IsEnabled = false;
        bool revoked;
        try { revoked = await _chatGpt.SignOutAsync(); }
        catch (ApiException ex)
        {
            ShowToast(ex.Message, ToastKind.Error);
            return;
        }
        finally { ChatGptSignOutButton.IsEnabled = true; }
        _modelLists.Remove(AgentProvider.ChatGpt);
        _state.ModelCatalogs?.Remove(AgentProvider.ChatGpt);
        _state.Save();
        if (_config.Provider == AgentProvider.ChatGpt && _cts == null) ApplyProvider();
        if (IsShown(SettingsOverlay))
        {
            KeepProviderInput();
            ShowProviderSettings();
        }
        if (!revoked)
            ShowToast("Вход удалён из программы, но OpenAI не подтвердил его отзыв. Отключить Sharp Agent можно в настройках ChatGPT.",
                ToastKind.Warning, "Открыть", () => FileActions.OpenUrl(ChatGptClient.UsageUrl));
    }

    private void CloseSettings()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        KeyPasswordBox.Clear();
        KeyTextBox.Clear();
        VoiceKeyBox.Clear();
        _settingsKeys.Clear();
        InputBox.Focus();
    }

    private void SettingsCancel_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void SettingsFields_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is Control field) field.Tag = null;
    }

    private string EnteredKey() => AppConfig.CleanKey(ShowKeyToggle.IsChecked == true ? KeyTextBox.Text : KeyPasswordBox.Password);

    private void ShowKey_Changed(object sender, RoutedEventArgs e)
    {
        bool show = ShowKeyToggle.IsChecked == true;
        if (show) KeyTextBox.Text = KeyPasswordBox.Password;
        else KeyPasswordBox.Password = KeyTextBox.Text;
        KeyTextBox.Visibility = VisibleIf(show);
        KeyPasswordBox.Visibility = VisibleIf(!show);
        AutomationProperties.SetName(ShowKeyToggle, show ? "Скрыть ключ" : "Показать ключ");
    }

    private async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var provider = _settingsProvider;
        var key = EnteredKey();
        if (provider == AgentProvider.ChatGpt ? !_chatGpt.PlanAllowed : key.Length == 0)
        {
            SetTestResult(provider != AgentProvider.ChatGpt ? "Сначала вставьте ключ."
                : _chatGpt.Connected ? "Нажмите «Разрешить использование подписки» или выберите другого провайдера."
                : "Сначала войдите: нажмите «Продолжить с ChatGPT».", false);
            return;
        }
        using IChatProvider client = provider switch
        {
            AgentProvider.ChatGpt => new ChatGptClient(_chatGpt),
            AgentProvider.Tooken => new ChatApiClient(AppConfig.NormalizeBaseUrl(BaseUrlBox.Text.Trim() is { Length: > 0 } url ? url : AppConfig.DefaultBaseUrl), key),
            _ => new ChatApiClient(AppConfig.DefaultBaseUrlOf(provider), key, provider),
        };
        TestKeyButton.IsEnabled = false;
        SetTestResult("Проверяю…", null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (provider == AgentProvider.OpenRouter && client is ChatApiClient router) await router.CheckOpenRouterKeyAsync(cts.Token);
            var models = await client.ListModelsAsync(cts.Token);
            RememberModelList(provider, models);
            if (provider != _settingsProvider || !IsShown(SettingsOverlay)) return;
            var count = models.Count(m => m.Chat);
            SetTestResult((provider == AgentProvider.ChatGpt ? "Вход выполнен." : "Ключ принят.") + (count > 0 ? $" Доступно моделей: {count}." : ""), true);
            if (count > 0) DefaultModelButton.Tag = _modelLists[provider];
            if (provider == AgentProvider.Tooken && _imageModels is { Count: > 0 }) ImageModelButton.Tag = _imageModels;
        }
        catch (Exception ex)
        {
            if (provider != _settingsProvider || !IsShown(SettingsOverlay)) return;
            SetTestResult(ex switch
            {
                ApiException => ex.Message,
                OperationCanceledException => "Сервер не ответил за 20 секунд. Проверьте адрес API и подключение к интернету.",
                _ => "Не удалось подключиться: " + ex.Message,
            }, false);
        }
        finally
        {
            TestKeyButton.IsEnabled = true;
            if (provider == AgentProvider.ChatGpt) ShowChatGptAccount();
            if (_provider is ChatGptClient && !_chatGpt.PlanAllowed && _cts == null) ApplyProvider();
        }
    }

    private void DefaultModelButton_Click(object sender, RoutedEventArgs e) => PickModel(DefaultModelButton, _settingsProvider);

    private void ImageModelButton_Click(object sender, RoutedEventArgs e) => PickModel(ImageModelButton, AgentProvider.Tooken);

    private void SetTestResult(string text, bool? ok)
    {
        TestKeyResult.Text = text;
        TestKeyResult.Visibility = VisibleIf(text.Length > 0);
        (TestKeyResult.Foreground, TestKeyIcon.Text) = ok switch { true => (Theme.Success, Glyphs.Check), false => (Theme.DangerText, Glyphs.Warning), _ => (Theme.TextMuted, "") };
        TestKeyIcon.Foreground = ok == true ? Theme.Success : Theme.DangerText;
        AutomationProperties.SetLiveSetting(TestKeyResult, AutomationLiveSetting.Polite);
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        Control? invalid = null;
        void Fail(string message, Control field)
        {
            field.Tag = "invalid";
            if (invalid == null) ShowSettingsError(message);
            invalid ??= field;
        }
        var maxTokens = MaxTokensBox.Text.Trim();
        if (maxTokens.Length > 0 && (!int.TryParse(maxTokens, out var limit) || limit < 1))
            Fail("«Токенов в одном ответе» — целое число больше нуля или пустое поле.", MaxTokensBox);
        int SubagentLimit(TextBox field, int current, int max, string label)
        {
            if (SubagentsToggle.IsChecked != true) return current;
            if (int.TryParse(field.Text, out int number) && number >= 1 && number <= max) return number;
            Fail($"«{label}» — целое число от 1 до {max}.", field);
            return current;
        }
        int subagentTasks = SubagentLimit(SubagentTaskLimitBox, _config.SubagentTaskLimit, AppConfig.MaxSubagentTasks, "Поручений на запрос");
        int subagentSteps = SubagentLimit(SubagentStepLimitBox, _config.SubagentStepLimit, AppConfig.MaxSubagentSteps, "Шагов на поручение");
        var url = BaseUrlBox.Text.Trim() is { Length: > 0 } text ? text : AppConfig.DefaultBaseUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            Fail("Адрес API tooken.club должен начинаться с https://" + (_settingsProvider == AgentProvider.Tooken ? "" : " — исправьте его в разделе Tooken Club."), BaseUrlBox);
        var dayLimit = DayLimitBox.Text.Trim();
        if (dayLimit.Length > 0 && AppConfig.ParseMillions(dayLimit) == null)
            Fail("«Лимит в день» — число миллионов токенов, например 5 или 0,5. Чтобы снять лимит, оставьте поле пустым.", DayLimitBox);
        var monthLimit = MonthLimitBox.Text.Trim();
        if (monthLimit.Length > 0 && AppConfig.ParseMillions(monthLimit) == null)
            Fail("«Лимит в месяц» — число миллионов токенов, например 50. Чтобы снять лимит, оставьте поле пустым.", MonthLimitBox);
        if (invalid != null)
        {
            invalid.BringIntoView();
            invalid.Focus();
            return;
        }
        KeepProviderInput();
        var values = new Dictionary<string, string?>
        {
            ["AGENT_PROVIDER"] = _settingsProvider.ToString().ToLowerInvariant(), ["AGENT_USE_SUBAGENTS"] = SubagentsToggle.IsChecked == true ? "true" : "false",
            ["AGENT_COMPUTER_USE"] = ComputerUseToggle.IsChecked == true ? "true" : "false",
            ["AGENT_SUBAGENT_TASK_LIMIT"] = subagentTasks.ToString(CultureInfo.InvariantCulture),
            ["AGENT_SUBAGENT_STEP_LIMIT"] = subagentSteps.ToString(CultureInfo.InvariantCulture),
            ["TOOKEN_BASE_URL"] = url, ["TOOKEN_IMAGE_MODEL"] = ImageModelButton.Content as string is { Length: > 0 } image ? image : AppConfig.DefaultImageModel,
            ["TOOKEN_MAX_TOKENS"] = maxTokens, ["AGENT_DAY_LIMIT_MLN"] = dayLimit.Replace(',', '.'), ["AGENT_MONTH_LIMIT_MLN"] = monthLimit.Replace(',', '.'),
        };
        foreach (var (provider, key) in _settingsKeys) values[AppConfig.KeyOf(provider, "API_KEY")] = key;
        foreach (var (provider, model) in _settingsModels)
            if (provider == _settingsProvider || model != _savedModels.GetValueOrDefault(provider))
                values[AppConfig.KeyOf(provider, "MODEL")] = model.Length > 0 ? model : AppConfig.DefaultModelOf(provider);
        if (SaveEnv(values) is { } error)
        {
            ShowSettingsError("Не удалось сохранить файл .env: " + error);
            return;
        }
        _state.LimitOverrideDay = null;
        ApplyConfig();
        CloseSettings();
        ShowToast(_provider != null ? "Настройки сохранены" : "Сохранено, но " + (_config.Provider switch
        {
            AgentProvider.ChatGpt => "подписка ChatGPT не подключена",
            AgentProvider.Groq => "API-ключ Groq пуст",
            _ => "API-ключ пуст",
        }) + " — агент не сможет отвечать", _provider != null ? ToastKind.Success : ToastKind.Warning);
    }

    private void ShowSettingsError(string message)
    {
        SettingsError.Text = message;
        SettingsError.Visibility = Visibility.Visible;
        AutomationProperties.SetLiveSetting(SettingsError, AutomationLiveSetting.Assertive);
    }

    private void KeyLink_Click(object sender, RoutedEventArgs e) =>
        FileActions.OpenUrl(_settingsProvider switch
        {
            AgentProvider.Groq => Groq.KeysUrl,
            AgentProvider.Gemini => "https://aistudio.google.com/apikey",
            AgentProvider.OpenRouter => "https://openrouter.ai/settings/keys",
            AgentProvider.Mistral => "https://console.mistral.ai/api-keys",
            _ => "https://tooken.club/dashboard",
        });

    private void ThemePill_Checked(object sender, RoutedEventArgs e)
    {
        var mode = (string)((RadioButton)sender).Tag is var tag && tag == "dark" ? null : tag;
        if (_state.Theme == mode) return;
        _state.Theme = mode;
        _state.Save();
        ApplyTheme();
    }

    private void GroqKeysLink_Click(object sender, RoutedEventArgs e) => FileActions.OpenUrl(Groq.KeysUrl);

    private void OpenPlugins_Click(object sender, RoutedEventArgs e)
    {
        var folder = _toolContext.PluginsFolder!;
        try
        {
            Directory.CreateDirectory(folder);
            FileActions.OpenFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowToast("Не удалось создать папку плагинов: " + ex.Message, ToastKind.Error); }
    }
}
