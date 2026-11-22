using System.Windows.Documents;
using System.Windows.Navigation;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    enum ToastKind { Info, Success, Warning, Error }

    sealed record BannerInfo(string Glyph, string Title, string Text, string ActionText, Action Action, bool Warning);

    readonly Dictionary<string, BannerInfo> _banners = new();
    readonly Dictionary<AgentProvider, string> _settingsKeys = new(), _settingsModels = new(), _savedModels = new();
    AgentProvider _settingsProvider;
    readonly Dictionary<AgentProvider, string> _settingsImageModels = [], _savedImageModels = [];
    static (string? Value, string Label)[] Themes => [(null, Tr("Тёмная", "Dark")), ("light", Tr("Светлая", "Light")), ("system", Tr("Как в Windows", "Follow Windows"))];
    static (bool Value, string Label)[] ImageSources => [(false, "Tooken Club"), (true, Tr("Как в чате", "Same as chat"))];
    bool _settingsImagesLikeChat;
    string? _settingsTheme;
    FrameworkElement? _settingsPage;
    bool _settingsRussian;
    bool _settingsLargeChatText;
    CancellationTokenSource? _signIn;
    bool _chatGptSigningOut, _testingKey;
    string? _chatGptAccountChoice;
    DispatcherTimer? _toastTimer;
    Action? _toastAction;


    static Hyperlink MakeLink(string text, Action click)
    {
        var link = new Hyperlink(new Run(text));
        link.Click += (_, _) => click();
        return link;
    }

    void ShowToast(string text, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
    {
        _compactChat?.SetStatus(text.ReplaceLineEndings(" "),
            kind switch { ToastKind.Success => "Success", ToastKind.Warning => "Warning", ToastKind.Error => "DangerText", _ => "TextMuted" });
        ToastText.Text = text;
        (ToastIcon.Text, var brush) = kind switch
        {
            ToastKind.Success => (Glyphs.Check, "Success"),
            ToastKind.Warning => (Glyphs.Warning, "Warning"),
            ToastKind.Error => (Glyphs.Warning, "DangerText"),
            _ => (Glyphs.Info, "AccentText"),
        };
        ToastIcon.SetResourceReference(TextBlock.ForegroundProperty, brush);
        _toastAction = action;
        ToastAction.Content = actionText;
        ToastAction.Visibility = VisibleIf(action != null && actionText != null);
        ToastHost.IsHitTestVisible = action != null;
        AutomationProperties.SetLiveSetting(ToastText, kind == ToastKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        ToastHost.Visibility = Visibility.Visible;
        if (_toastTimer == null) (_toastTimer = new()).Tick += (_, _) => HideToast();
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(2 + text.Length / 15.0, 3, 12) + (action != null ? 4 : 0));
        _toastTimer.Start();
    }

    void HideToast()
    {
        _toastTimer?.Stop();
        _toastAction = null;
        ToastHost.Visibility = Visibility.Collapsed;
    }

    void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        HideToast();
        action?.Invoke();
    }

    void ShowBanner(string key, string glyph, string title, string text, string actionText, Action action, bool warning = false)
    {
        _banners[key] = new(glyph, title, text, actionText, action, warning);
        RenderBanner();
    }

    void HideBanner(string key)
    {
        if (_banners.Remove(key)) RenderBanner();
    }

    BannerInfo? TopBanner => _banners.Values.OrderByDescending(b => b.Warning).FirstOrDefault();

    void RenderBanner()
    {
        var banner = TopBanner;
        Banner.Visibility = VisibleIf(banner != null);
        if (banner == null) return;
        BannerIcon.Text = banner.Glyph;
        BannerTitle.Text = banner.Title;
        BannerText.Text = banner.Text;
        BannerAction.Content = banner.ActionText;
        BannerIcon.Paint(TextBlock.ForegroundProperty, banner.Warning ? "Warning" : "AccentText");
    }

    void BannerAction_Click(object sender, RoutedEventArgs e) => TopBanner?.Action();

    void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    void ShowSettings() => OpenSettings(_provider == null ? KeyField : null);

    void ShowKeySettings() => OpenSettings(KeyField);

    Control KeyField => _config.Provider != AgentProvider.ChatGpt ? KeyPasswordBox
        : _chatGpt.SavedAccounts.Any() ? ChatGptAccountButton : ChatGptSignInButton;

    void OpenSettings(Control? focus)
    {
        if (WaitForChatFile(ChatFileBusy)) return;
        if (AnyRunning)
        {
            ShowToast(Tr("Остановите агента, чтобы изменить настройки", "Stop active tasks before changing settings"), ToastKind.Warning);
            return;
        }
        if (SettingsOpen) return;
        CloseFlyout();
        CloseOverlay();
        SettingsError.Visibility = Visibility.Collapsed;
        var env = new Dictionary<string, string>();
        bool unreadable = false;
        try { if (File.Exists(AppPaths.EnvPath)) env = DotEnv.Parse(AppPaths.EnvPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowSettingsError(Tr("Не удалось прочитать .env: ", "Could not read .env: ") + ex.Message);
            unreadable = true;
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
                _settingsKeys[provider] = AppConfig.CleanKey(unreadable ? _config.ApiKeyFor(provider) : Value(AppConfig.KeyOf(provider, "API_KEY")));
            _settingsModels[provider] = _savedModels[provider] =
                provider == _config.Provider ? CurrentModel() : AppConfig.ChatModelOf(provider, env.GetValueOrDefault(AppConfig.KeyOf(provider, "MODEL")));
        }
        foreach (var provider in AppConfig.ImageProviders)
            _settingsImageModels[provider] = _savedImageModels[provider] = Value(AppConfig.KeyOf(provider, "IMAGE_MODEL"), AppConfig.ImageModelsOf(provider)[0]);
        _settingsImagesLikeChat = _config.ImagesLikeChat;
        ShowChoice(ImageSourceButton, ImageSources, _settingsImagesLikeChat);
        SubagentsToggle.IsChecked = _config.UseSubagents;
        AutoChatTitlesToggle.IsChecked = _config.AutoChatTitles;
        ComputerUseToggle.IsChecked = _config.ComputerUse;
        _settingsTheme = _state.Theme;
        ShowChoice(ThemeButton, Themes, _settingsTheme);
        _settingsRussian = _config.RussianLanguage;
        ShowChoice(LanguageButton, Languages, _settingsRussian);
        _settingsLargeChatText = _state.LargeChatText;
        ShowChoice(TextSizeButton, TextSizes, _settingsLargeChatText);
        BaseUrlBox.Text = Value("TOOKEN_BASE_URL", AppConfig.DefaultBaseUrl);
        LoadNetworkSettings(unreadable ? _config.Network : NetworkSettings.Load(key => env.GetValueOrDefault(key)), Value("AGENT_VPN_PROFILES"));
        var plugins = PluginTools.Load(_toolContext.PluginsFolder);
        PluginsText.Text = plugins.Count > 0
            ? Tr($"Установлены: {string.Join(", ", plugins.Select(p => p.Name))}. Агент сам выбирает подходящий плагин по задаче.", $"Installed: {string.Join(", ", plugins.Select(p => p.Name))}. The agent chooses the plugin that fits the task.")
            : Tr("Плагинов нет. Плагин — это папка с файлом PLUGIN.md внутри папки plugins.", "No plugins installed. A plugin is a folder containing PLUGIN.md inside the plugins folder.");
        MaxTokensBox.Text = Value("TOOKEN_MAX_TOKENS");
        DayLimitBox.Text = _config.DayLimit is { } day ? Tokens.ToMillions(day) : "";
        MonthLimitBox.Text = _config.MonthLimit is { } month ? Tokens.ToMillions(month) : "";
        ClearInvalidFields();
        _settingsProvider = _config.Provider;
        if (_signIn == null) _chatGptAccountChoice = _chatGpt.ClientId;
        ShowProviderSettings();
        ShowSettingsPage(focus ?? _settingsPage ?? SettingsGeneral);
        WorkArea.Visibility = Sidebar.Visibility = Visibility.Hidden;
        SettingsPage.Visibility = Visibility.Visible;
        Dispatcher.InvokeAsync(() =>
        {
            if (!SettingsOpen) return;
            focus?.BringIntoView();
            (focus ?? SettingsNav.Children.OfType<Button>().First(nav => Equals(nav.Tag, "current"))).Focus();
        }, DispatcherPriority.Input);
    }

    bool SettingsOpen => IsShown(SettingsPage);

    void ShowSettingsPage(FrameworkElement target)
    {
        var page = _settingsPage = SettingsFields.Children.OfType<FrameworkElement>().First(page => page == target || page.IsAncestorOf(target));
        foreach (var child in SettingsFields.Children.OfType<FrameworkElement>()) child.Visibility = VisibleIf(child == page);
        foreach (var nav in SettingsNav.Children.OfType<Button>().Where(nav => nav.CommandParameter != null))
            nav.Tag = Equals(nav.CommandParameter, page.Name) ? "current" : null;
        SettingsScroll.ScrollToTop();
    }

    void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        var nav = (Button)sender;
        ShowSettingsPage((FrameworkElement)FindName((string)nav.CommandParameter));
        nav.Focus();
    }

    // Dropdown rows show the current choice; their menu marks it with ✓.
    static void ShowChoice<T>(Button button, (T Value, string Label)[] choices, T value) =>
        button.Content = Array.Find(choices, choice => Equals(choice.Value, value)).Label ?? choices[0].Label;

    static void PickChoice<T>(Button button, (T Value, string Label)[] choices, T current, Action<T> choose)
    {
        var menu = new ContextMenu { PlacementTarget = button, MaxHeight = 320, MinWidth = button.ActualWidth };
        foreach (var (value, label) in choices)
        {
            var item = FileActions.Item(label.Replace("_", "__"), null, () => choose(value));
            item.IsChecked = Equals(value, current);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    void ProviderButton_Click(object sender, RoutedEventArgs e) =>
        PickChoice(ProviderButton, ProvidersByName().Select(provider => (provider, ProviderName(provider))).ToArray(), _settingsProvider, provider =>
        {
            if (provider == _settingsProvider) return;
            KeepProviderInput();
            NetworkTestStatus.Text = "";
            _settingsProvider = provider;
            ShowProviderSettings();
        });

    void KeepProviderInput()
    {
        if (_settingsProvider != AgentProvider.ChatGpt) _settingsKeys[_settingsProvider] = EnteredKey();
        if (_settingsProvider != AgentProvider.Groq) _settingsKeys[AgentProvider.Groq] = AppConfig.CleanKey(VoiceKeyBox.Password);
        _settingsModels[_settingsProvider] = DefaultModelButton.Content as string ?? "";
    }

    void ShowProviderSettings()
    {
        var provider = _settingsProvider;
        bool keyed = provider != AgentProvider.ChatGpt, groq = provider == AgentProvider.Groq;
        ProviderButton.Content = ProviderName(provider);
        TestKeyButton.Content = keyed ? Tr("Проверить API-ключ", "Check API key") : Tr("Проверить вход", "Check sign-in");
        TestKeyButton.IsEnabled = !_testingKey;
        KeyPanel.Visibility = VisibleIf(keyed);
        ChatGptPanel.Visibility = VisibleIf(!keyed);
        TookenPanel.Visibility = VisibleIf(provider == AgentProvider.Tooken);
        ProviderKeysHint.Visibility = VisibleIf(keyed);
        ProviderKeysLink.NavigateUri = new Uri(provider switch
        {
            AgentProvider.Groq => Groq.KeysUrl,
            AgentProvider.Gemini => "https://aistudio.google.com/apikey",
            AgentProvider.OpenRouter => "https://openrouter.ai/settings/keys",
            AgentProvider.Mistral => "https://console.mistral.ai/api-keys",
            AgentProvider.OpenAI => "https://platform.openai.com/api-keys",
            AgentProvider.Anthropic => "https://platform.claude.com/settings/keys",
            _ => "https://tooken.club/dashboard",
        });
        if (keyed)
        {
            ShowKeyToggle.IsChecked = false;
            KeyTextBox.Text = KeyPasswordBox.Password = _settingsKeys.GetValueOrDefault(provider, "");
            KeyLabel.Text = provider switch
            {
                AgentProvider.OpenAI => Tr("Ключ OpenAI API", "OpenAI API key"),
                AgentProvider.Anthropic => Tr("Ключ Claude API", "Claude API key"),
                _ => Tr("API-ключ ", "API key for ") + ProviderName(provider),
            };
            KeyHint.Text = provider switch
            {
                AgentProvider.Groq => Tr("Ключ создаётся в консоли Groq и начинается с gsk_.", "Create a key in the Groq console. It starts with gsk_."),
                AgentProvider.Tooken => Tr("Ключ выдаётся в личном кабинете и начинается с tc_live_.", "Get a key from your account dashboard. It starts with tc_live_."),
                AgentProvider.OpenAI => Tr("Создайте ключ на платформе OpenAI. Запросы API оплачиваются отдельно от подписки ChatGPT.",
                    "Create a key on the OpenAI platform. API requests are billed separately from your ChatGPT subscription."),
                _ => Tr("Создайте API-ключ в кабинете провайдера.", "Create an API key from the provider’s dashboard."),
            };
            AutomationProperties.SetName(KeyPasswordBox, KeyLabel.Text);
            AutomationProperties.SetName(KeyTextBox, KeyLabel.Text);
        }
        else ShowChatGptAccount();
        VoiceKeyPanel.Visibility = VisibleIf(!groq);
        if (!groq) VoiceKeyBox.Password = _settingsKeys.GetValueOrDefault(AgentProvider.Groq, "");
        VoiceHint.Text = groq
            ? Tr("Микрофон в поле ввода отправляет запись в Groq по API-ключу выше и вставляет распознанный текст. Запись нигде не сохраняется. ", "The microphone sends your recording to Groq using the API key above and inserts the transcription. The recording is not saved. ")
            : Tr("Микрофон в поле ввода отправляет запись в Groq и вставляет распознанный текст — при любом провайдере. Запись нигде не сохраняется. ", "The microphone sends your recording to Groq and inserts the transcription with any chat provider. The recording is not saved. ") +
              Tr("Бесплатный ключ создаётся в консоли Groq и начинается с gsk_. ", "Create a free key in the Groq console. It starts with gsk_. ");
        DefaultModelButton.Content = _settingsModels.GetValueOrDefault(provider) is { Length: > 0 } model ? model : AppConfig.DefaultModelOf(provider);
        if (_state.ModelCatalogs?.GetValueOrDefault(provider) is { Models.Count: > 0 } catalog && catalog.Connection == SettingsCatalogKey())
        {
            RememberModelList(provider, catalog.Models);
            DefaultModelButton.Tag = ChatModelChoices(provider);
        }
        else DefaultModelButton.Tag = DefaultModelsOf(provider);
        SetTestResult("", null);
        ShowImageSettings();
    }

    AgentProvider ImageTarget => _settingsImagesLikeChat && _settingsProvider != AgentProvider.Tooken &&
        AppConfig.ImageProviders.Contains(_settingsProvider) ? _settingsProvider : AgentProvider.Tooken;

    void ShowImageSettings()
    {
        var target = ImageTarget;
        var model = _settingsImageModels[target];
        IEnumerable<string> models = target == AgentProvider.Tooken && _imageModels is { Count: > 0 } known ? known : AppConfig.ImageModelsOf(target);
        ImageModelButton.Content = model;
        ImageModelButton.Tag = models.Contains(model) ? models : models.Prepend(model);
        ImageModelHint.Text = target != AgentProvider.Tooken ? Tr($"Картинки рисует {ProviderName(target)} по ключу из этого раздела.", $"{ProviderName(target)} generates images using the API key in this section.")
            : _settingsProvider == AgentProvider.Tooken ? Tr("Картинки рисует Tooken Club по его ключу и балансу.", "Image generation uses the Tooken Club API key and balance.")
            : _settingsImagesLikeChat
                ? Tr($"{ProviderName(_settingsProvider)} не рисует картинки через API, поэтому их рисует Tooken Club по его ключу и балансу.", $"{ProviderName(_settingsProvider)} has no image generation API. Images use the Tooken Club API key and balance.")
                : Tr("Картинки рисует Tooken Club по его ключу и балансу при любом провайдере чата.", "Image generation uses the Tooken Club API key and balance with any chat provider.");
    }

    void ImageSourceButton_Click(object sender, RoutedEventArgs e) => PickChoice(ImageSourceButton, ImageSources, _settingsImagesLikeChat, likeChat =>
    {
        _settingsImagesLikeChat = likeChat;
        ShowChoice(ImageSourceButton, ImageSources, likeChat);
        ShowImageSettings();
    });

    (string? Value, string Label)[] ChatGptAccountChoices() => _chatGpt.SavedAccounts
        .Select(account => ((string?)account.ClientId, account.Label ?? Tr("Аккаунт ChatGPT", "ChatGPT account")))
        .Append((null, Tr("Другой аккаунт…", "Another account…"))).ToArray();

    void ShowChatGptAccount()
    {
        var choices = ChatGptAccountChoices();
        if (!choices.Any(choice => choice.Value == _chatGptAccountChoice)) _chatGptAccountChoice = _chatGpt.ClientId;
        bool current = _chatGptAccountChoice == _chatGpt.ClientId;
        bool connected = _chatGpt.Connected, allowed = _chatGpt.PlanAllowed, waiting = _signIn != null;
        bool busy = waiting || _chatGptSigningOut;
        if (_settingsProvider == AgentProvider.ChatGpt) TestKeyButton.IsEnabled = !_testingKey && !busy && current;
        ChatGptAccountButton.Visibility = VisibleIf(choices.Length > 1);
        ChatGptAccountButton.IsEnabled = !busy;
        ShowChoice(ChatGptAccountButton, choices, _chatGptAccountChoice);
        var label = (string)ChatGptAccountButton.Content;
        ChatGptAccountButton.ToolTip = label;
        AutomationProperties.SetName(ChatGptAccountButton, Tr("Аккаунт ChatGPT: ", "ChatGPT account: ") + label);
        ChatGptPlan.Text = !current && connected && _chatGpt.Label is { } active
            ? Tr($"Сейчас подключён {active}", $"Currently connected: {active}")
            : current && allowed ? Tr("Подписка ChatGPT подключена", "ChatGPT subscription connected")
            : current && connected ? Tr("Использование подписки не разрешено", "Subscription access was not granted")
            : _chatGptAccountChoice != null ? Tr("Для этого аккаунта нужен вход", "This account needs sign-in") : "";
        ChatGptSignInButton.Visibility = VisibleIf(waiting || !current || !allowed);
        ChatGptSignInButton.IsEnabled = !busy;
        ChatGptSignInButton.Content = waiting ? Tr("Ожидаю вход в браузере…", "Waiting for browser sign-in…")
            : current && connected ? Tr("Разрешить использование подписки", "Allow subscription access")
            : Tr("Продолжить с ChatGPT", "Continue with ChatGPT");
        ChatGptCancelButton.Visibility = VisibleIf(waiting);
        ChatGptSignOutButton.Visibility = VisibleIf(connected && current && !waiting);
        ChatGptSignOutButton.IsEnabled = !busy;
        ChatGptHint.Text = waiting ? Tr("Завершите вход на сайте OpenAI — программа ждёт до 10 минут.", "Finish signing in on the OpenAI site within 10 minutes.")
            : _chatGptSigningOut ? Tr("Выполняется выход из ChatGPT…", "Signing out of ChatGPT…")
            : current && allowed ? Tr("Запросы агента расходуют вашу подписку ChatGPT.", "Agent requests count toward your ChatGPT subscription.")
            : current && connected ? Tr("Разрешите Sharp Agent использовать подписку на сайте OpenAI.", "Allow Sharp Agent to use your subscription on the OpenAI site.")
            : Tr("Откроется сайт OpenAI. Войдите в ChatGPT и разрешите Sharp Agent использовать подписку.", "The OpenAI site will open. Sign in to ChatGPT and allow Sharp Agent to use your subscription.");
    }

    void ChatGptAccountButton_Click(object sender, RoutedEventArgs e) =>
        PickChoice(ChatGptAccountButton, ChatGptAccountChoices(), _chatGptAccountChoice, account =>
        {
            _chatGptAccountChoice = account;
            SetTestResult("", null);
            ShowChatGptAccount();
        });

    async void ChatGptSignIn_Click(object sender, RoutedEventArgs e) => await SignInChatGptAsync(_chatGptAccountChoice);

    async Task SignInChatGptAsync(string? clientId)
    {
        if (_signIn != null || _chatGptSigningOut || AnyRunning) return;
        var cts = _signIn = new CancellationTokenSource();
        ShowChatGptAccount();
        SetTestResult("", null);
        bool welcome = false, checkSignIn = false;
        try
        {
            welcome = await _chatGpt.SignInAsync(OpenSignInPage, clientId, cts.Token);
            _chatGptAccountChoice = _chatGpt.ClientId;
            WindowEffects.Flash(this);
            if (_config.Provider == AgentProvider.ChatGpt) ApplyProvider();
            else if (_chatGpt.PlanAllowed && _cts == null) await SwitchProviderAsync(AgentProvider.ChatGpt);
            bool allowed = _chatGpt.PlanAllowed;
            if (!SettingsOpen)
                ShowToast(allowed ? Tr("Подписка ChatGPT подключена", "ChatGPT subscription connected") + (_chatGpt.Label is { } label ? ": " + label : "")
                    : Tr("Вход выполнен, но Sharp Agent не получил разрешения использовать подписку ChatGPT", "Signed in. Sharp Agent still needs permission to use the ChatGPT subscription."), allowed ? ToastKind.Success : ToastKind.Warning);
            else checkSignIn = _settingsProvider == AgentProvider.ChatGpt && allowed;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ApiException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            var text = ex is ApiException ? ex.Message : Tr("Не удалось войти в ChatGPT: ", "Could not sign in to ChatGPT: ") + ex.Message + Tr(" Попробуйте ещё раз.", " Try again.");
            if (SettingsOpen && _settingsProvider == AgentProvider.ChatGpt) SetTestResult(text, false);
            else ShowToast(text, ToastKind.Error);
            if (_provider is ChatGptClient && !_chatGpt.PlanAllowed) ApplyProvider();
        }
        finally
        {
            cts.Dispose();
            _signIn = null;
            ShowChatGptAccount();
        }
        if (checkSignIn) TestKey_Click(this, new RoutedEventArgs());
        if (welcome) ShowPlanWelcome();
    }

    static void OpenSignInPage(string url)
    {
        if (AppNetwork.CustomRoute) FileActions.RaiseNotify(Tr("Для входа откроется внешний браузер с его сетевыми настройками.", "Sign-in opens in an external browser using that browser’s network settings."), false);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new ApiException(Tr("Не удалось открыть браузер для входа в ChatGPT. Выберите браузер по умолчанию в параметрах Windows и нажмите «Продолжить с ChatGPT» ещё раз.", "Could not open the browser to sign in. Set a default browser in Windows Settings and select Continue with ChatGPT again."));
        }
    }

    void ChatGptCancel_Click(object sender, RoutedEventArgs e) => _signIn?.Cancel();

    async void ChatGptSignOut_Click(object sender, RoutedEventArgs e)
    {
        if (_signIn != null || _chatGptSigningOut || AnyRunning) return;
        if (!DarkDialog.Ask(this, Tr("Выйти из ChatGPT?", "Sign out of ChatGPT?"),
            Tr("Вход будет удалён, но аккаунт останется в списке. Чтобы подключить его снова, войдите через браузер.",
                "The session will be removed, but the account will stay in the list. Sign in again in your browser to reconnect it."),
            Tr("Выйти", "Sign out"))) return;
        _chatGptSigningOut = true;
        ShowChatGptAccount();
        bool revoked;
        try { revoked = await _chatGpt.SignOutAsync(); }
        catch (ApiException ex)
        {
            ShowToast(ex.Message, ToastKind.Error);
            return;
        }
        finally
        {
            _chatGptSigningOut = false;
            ShowChatGptAccount();
        }
        _modelLists.Remove(AgentProvider.ChatGpt);
        _state.ModelCatalogs?.Remove(AgentProvider.ChatGpt);
        _state.Save();
        if (_config.Provider == AgentProvider.ChatGpt && _cts == null) ApplyProvider();
        if (SettingsOpen)
        {
            KeepProviderInput();
            ShowProviderSettings();
        }
        if (!revoked)
            ShowToast(Tr("Вход удалён из программы, но OpenAI не подтвердил его отзыв. Отключить Sharp Agent можно в настройках ChatGPT.", "The session was removed from the app. OpenAI did not confirm revocation. You can revoke access in ChatGPT settings."),
                ToastKind.Warning, Tr("Открыть", "Open"), () => FileActions.OpenUrl(ChatGptClient.UsageUrl));
    }

    void CloseSettings()
    {
        _networkGeneration++;
        SettingsPage.Visibility = Visibility.Collapsed;
        WorkArea.Visibility = Sidebar.Visibility = Visibility.Visible;
        KeyPasswordBox.Clear();
        KeyTextBox.Clear();
        VoiceKeyBox.Clear();
        _settingsKeys.Clear();
        if (_settingsTheme != _state.Theme) ApplyTheme();
        InputBox.Focus();
    }

    void SettingsCancel_Click(object sender, RoutedEventArgs e) => CloseSettings();

    void SettingsFields_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (e.OriginalSource is Control field) field.Tag = null;
    }

    void ClearInvalidFields()
    {
        foreach (var field in new Control[] { MaxTokensBox, BaseUrlBox, DayLimitBox, MonthLimitBox, NetworkHost, NetworkPort, NetworkUser, NetworkSubscription, NetworkEngine })
            field.Tag = null;
    }

    string EnteredKey() => AppConfig.CleanKey(ShowKeyToggle.IsChecked == true ? KeyTextBox.Text : KeyPasswordBox.Password);

    string SettingsTookenBaseUrl() => AppConfig.NormalizeBaseUrl(BaseUrlBox.Text.Trim() is { Length: > 0 } url ? url : AppConfig.DefaultBaseUrl);

    string? SettingsCatalogKey() => ModelCatalogKey(_settingsProvider, EnteredKey(), SettingsTookenBaseUrl());

    void ShowKey_Changed(object sender, RoutedEventArgs e)
    {
        bool show = ShowKeyToggle.IsChecked == true;
        if (show) KeyTextBox.Text = KeyPasswordBox.Password;
        else KeyPasswordBox.Password = KeyTextBox.Text;
        KeyTextBox.Visibility = VisibleIf(show);
        KeyPasswordBox.Visibility = VisibleIf(!show);
        AutomationProperties.SetName(ShowKeyToggle, show ? "Скрыть ключ" : Tr("Показать ключ", "Show API key"));
    }

    async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var provider = _settingsProvider;
        if (_testingKey || provider == AgentProvider.ChatGpt &&
            (_signIn != null || _chatGptSigningOut || _chatGptAccountChoice != _chatGpt.ClientId)) return;
        var key = EnteredKey();
        var baseUrl = SettingsTookenBaseUrl();
        var connection = ModelCatalogKey(provider, key, baseUrl);
        if (provider == AgentProvider.ChatGpt ? !_chatGpt.PlanAllowed : key.Length == 0)
        {
            SetTestResult(provider != AgentProvider.ChatGpt ? Tr("Сначала вставьте ключ.", "Enter your key first.")
                : _chatGpt.Connected ? Tr("Нажмите «Разрешить использование подписки» или выберите другого провайдера.", "Choose Allow subscription access or select another provider.")
                : Tr("Сначала войдите: нажмите «Продолжить с ChatGPT».", "Sign in first by selecting Continue with ChatGPT."), false);
            return;
        }
        using IChatProvider client = provider switch
        {
            AgentProvider.ChatGpt => new ChatGptClient(_chatGpt),
            AgentProvider.OpenAI => new OpenAiResponsesClient(key),
            AgentProvider.Anthropic => new AnthropicClient(key),
            AgentProvider.Tooken => new ChatApiClient(baseUrl, key),
            _ => new ChatApiClient(AppConfig.DefaultBaseUrlOf(provider), key, provider),
        };
        _testingKey = true;
        TestKeyButton.IsEnabled = false;
        SetTestResult(Tr("Проверяю…", "Checking…"), null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (provider == AgentProvider.OpenRouter && client is ChatApiClient router) await router.CheckOpenRouterKeyAsync(cts.Token);
            var models = await client.ListModelsAsync(cts.Token);
            if (provider != _settingsProvider || !SettingsOpen || provider == AgentProvider.ChatGpt && _chatGptAccountChoice != _chatGpt.ClientId) return;
            if (SettingsCatalogKey() != connection)
            {
                SetTestResult("", null);
                return;
            }
            if (models.Count > 0)
            {
                CacheModelCatalog(provider, connection, models);
                if (provider == _config.Provider && connection == ModelCatalogKey(provider))
                {
                    ApplyModelCatalog(provider, models);
                    UpdateModelEffort();
                }
                else RememberModelList(provider, models);
                DefaultModelButton.Tag = ChatModelChoices(provider);
            }
            int count = models.Count > 0 ? _modelLists[provider].Count : 0;
            SetTestResult((provider == AgentProvider.ChatGpt ? Tr("Вход выполнен.", "Signed in.") : Tr("Ключ принят.", "Key accepted.")) +
                          (count > 0 ? $" Доступно моделей: {count}. Нажмите на название модели, чтобы открыть список." : ""), true);
            if (provider == AgentProvider.Tooken) ShowImageSettings();
        }
        catch (Exception ex)
        {
            if (provider != _settingsProvider || !SettingsOpen || SettingsCatalogKey() != connection ||
                provider == AgentProvider.ChatGpt && _chatGptAccountChoice != _chatGpt.ClientId) return;
            SetTestResult(ex switch
            {
                ApiException { StatusCode: HttpStatusCode.Unauthorized } => provider == AgentProvider.ChatGpt
                    ? Tr("Провайдер отклонил авторизацию (HTTP 401). Войдите в ChatGPT заново.", "Authentication failed (HTTP 401). Sign in to ChatGPT again.")
                    : Tr("Провайдер отклонил авторизацию (HTTP 401). Проверьте API-ключ и выбранного провайдера.", "Authentication failed (HTTP 401). Check the API key and selected provider."),
                ApiException => ex.Message,
                OperationCanceledException => Tr("Сервер не ответил за 20 секунд. Проверьте адрес API и подключение к интернету.", "The server did not respond within 20 seconds. Check the API address and your internet connection."),
                _ => Tr("Не удалось подключиться: ", "Could not connect: ") + ex.Message,
            }, false);
        }
        finally
        {
            _testingKey = false;
            if (_settingsProvider == AgentProvider.ChatGpt) ShowChatGptAccount();
            else TestKeyButton.IsEnabled = true;
            if (_provider is ChatGptClient && !_chatGpt.PlanAllowed && _cts == null) ApplyProvider();
        }
    }

    void DefaultModelButton_Click(object sender, RoutedEventArgs e) => PickModel(DefaultModelButton, _settingsProvider);

    void ImageModelButton_Click(object sender, RoutedEventArgs e)
    {
        var target = ImageTarget;
        ModelMenu(ImageModelButton, target, model => ImageModelButton.Content = _settingsImageModels[target] = model).IsOpen = true;
    }

    void SetTestResult(string text, bool? ok)
    {
        TestKeyResult.Text = text;
        TestKeyResult.Visibility = VisibleIf(text.Length > 0);
        (var brush, TestKeyIcon.Text) = ok switch { true => ("Success", Glyphs.Check), false => ("DangerText", Glyphs.Warning), _ => ("TextMuted", "") };
        TestKeyResult.SetResourceReference(TextBlock.ForegroundProperty, brush);
        TestKeyIcon.SetResourceReference(TextBlock.ForegroundProperty, ok == true ? "Success" : "DangerText");
        AutomationProperties.SetLiveSetting(TestKeyResult, AutomationLiveSetting.Polite);
    }

    async void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        Control? invalid = null;
        void Fail(string message, Control field)
        {
            field.Tag = "invalid";
            if (invalid == null) ShowSettingsError(message);
            invalid ??= field;
        }
        ClearInvalidFields();
        var maxTokens = MaxTokensBox.Text.Trim();
        if (maxTokens.Length > 0 && (!int.TryParse(maxTokens, out var limit) || limit < 1))
            Fail(Tr("«Токенов в одном ответе» — целое число больше нуля или пустое поле.", "Enter a positive whole number for Tokens per reply, or leave it blank."), MaxTokensBox);
        var url = BaseUrlBox.Text.Trim() is { Length: > 0 } text ? text : AppConfig.DefaultBaseUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            Fail(Tr("Адрес API tooken.club должен начинаться с https://", "The tooken.club API address must begin with https://") + (_settingsProvider == AgentProvider.Tooken ? "" : Tr(" — выберите Tooken Club в разделе «Провайдеры» и исправьте адрес.", " Select Tooken Club under Providers to edit the address.")), BaseUrlBox);
        var dayLimit = DayLimitBox.Text.Trim();
        if (dayLimit.Length > 0 && AppConfig.ParseMillions(dayLimit) == null)
            Fail(Tr("«Лимит в день» — число миллионов токенов, например 5 или 0,5. Чтобы снять лимит, оставьте поле пустым.", "Enter the daily limit in millions of tokens, for example 5 or 0.5. Leave it blank for no limit."), DayLimitBox);
        var monthLimit = MonthLimitBox.Text.Trim();
        if (monthLimit.Length > 0 && AppConfig.ParseMillions(monthLimit) == null)
            Fail(Tr("«Лимит в месяц» — число миллионов токенов, например 50. Чтобы снять лимит, оставьте поле пустым.", "Enter the monthly limit in millions of tokens, for example 50. Leave it blank for no limit."), MonthLimitBox);
        var network = ReadNetworkSettings();
        try
        {
            network = network.Validate();
            if (invalid == null)
            {
                SettingsPage.IsEnabled = false;
                try
                {
                    if (await ApproveNetworkEngineAsync(network) is not { } approved) return;
                    network = approved;
                }
                finally { SettingsPage.IsEnabled = true; }
            }
        }
        catch (InvalidOperationException ex)
        {
            if (network.Mode == NetworkMode.Vpn) FindNetworkEngine();
            Fail(ex.Message, network switch
            {
                { Mode: NetworkMode.Vpn } => _vpnSelection == null ? NetworkSubscription : NetworkEngine,
                _ when Uri.CheckHostName(network.Host.Trim('[', ']')) == UriHostNameType.Unknown => NetworkHost,
                _ when !int.TryParse(network.Port, out var port) || port is < 1 or > 65_535 => NetworkPort,
                _ => NetworkUser,
            });
        }
        if (invalid != null)
        {
            ShowSettingsPage(invalid);
            SettingsScroll.UpdateLayout();
            invalid.BringIntoView();
            invalid.Focus();
            return;
        }
        KeepProviderInput();
        var values = new Dictionary<string, string?>
        {
            ["AGENT_PROVIDER"] = _settingsProvider.ToString().ToLowerInvariant(), ["AGENT_USE_SUBAGENTS"] = SubagentsToggle.IsChecked == true ? "true" : "false",
            ["AGENT_COMPUTER_USE"] = ComputerUseToggle.IsChecked == true ? "true" : "false",
            ["AGENT_AUTO_CHAT_TITLES"] = AutoChatTitlesToggle.IsChecked == true ? "true" : "false",
            ["AGENT_LANGUAGE"] = _settingsRussian ? "ru" : "en",
            ["TOOKEN_BASE_URL"] = url, ["AGENT_IMAGE_PROVIDER"] = _settingsImagesLikeChat ? "chat" : "tooken",
            ["TOOKEN_MAX_TOKENS"] = maxTokens, ["AGENT_DAY_LIMIT_MLN"] = dayLimit.Replace(',', '.'), ["AGENT_MONTH_LIMIT_MLN"] = monthLimit.Replace(',', '.'),
        };
        foreach (var (provider, key) in _settingsKeys) values[AppConfig.KeyOf(provider, "API_KEY")] = key;
        network.WriteTo(values);
        values["AGENT_VPN_PROFILES"] = string.Join(';', _vpnProfiles.Select(profile => profile.Save()));
        foreach (var (provider, model) in _settingsImageModels)
            if (model != _savedImageModels[provider])
                values[AppConfig.KeyOf(provider, "IMAGE_MODEL")] = model;
        foreach (var (provider, model) in _settingsModels)
            if (provider == _settingsProvider || model != _savedModels.GetValueOrDefault(provider))
                values[AppConfig.KeyOf(provider, "MODEL")] = model.Length > 0 ? model : AppConfig.DefaultModelOf(provider);
        var previous = CurrentModelSelection();
        if (SaveEnv(values) is { } error)
        {
            ShowSettingsError(Tr("Не удалось сохранить файл .env: ", "Could not save .env: ") + error);
            return;
        }
        _state.LimitOverrideDay = null;
        _state.Theme = _settingsTheme;
        bool textSizeChanged = _state.LargeChatText != _settingsLargeChatText;
        _state.LargeChatText = _settingsLargeChatText;
        ChatTextSize.Extra = _settingsLargeChatText ? 1 : 0;
        InputBox.FontSize = 14 + ChatTextSize.Extra;
        _state.Save();
        bool languageChanged = UiLanguage.Current.Value.IsRussian != _config.RussianLanguage;
        ApplyConfig();
        if (languageChanged)
        {
            UpdateConfirmToggle();
            ApplySidebar();
        }
        if (languageChanged || textSizeChanged) RenderCurrentChat();
        CloseSettings();
        await RunChatOperationAsync(() => ShowModelChangeAsync(previous));
        ShowToast(_provider != null ? Tr("Настройки сохранены", "Settings saved") : _config.Provider switch
        {
            AgentProvider.ChatGpt => Tr("Настройки сохранены. Для ответов агента подключите подписку ChatGPT.", "Settings saved. Connect a ChatGPT subscription to use the agent."),
            AgentProvider.Groq => Tr("Настройки сохранены. Для ответов агента укажите API-ключ Groq.", "Settings saved. Enter a Groq API key to use the agent."),
            _ => Tr("Настройки сохранены. Для ответов агента укажите API-ключ.", "Settings saved. Enter an API key to use the agent."),
        }, _provider != null ? ToastKind.Success : ToastKind.Warning);
    }

    void ShowSettingsError(string message)
    {
        SettingsError.Text = message;
        SettingsError.Visibility = Visibility.Visible;
        AutomationProperties.SetLiveSetting(SettingsError, AutomationLiveSetting.Assertive);
    }

    void KeyLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        FileActions.OpenUrl(e.Uri.AbsoluteUri);
    }

    void ThemeButton_Click(object sender, RoutedEventArgs e) => PickChoice(ThemeButton, Themes, _settingsTheme, PreviewTheme);

    static readonly (bool Value, string Label)[] Languages = [(false, "English"), (true, "Русский")];

    static (bool Value, string Label)[] TextSizes => [(false, Tr("Обычный", "Normal")), (true, Tr("Увеличенный", "Large"))];

    void TextSizeButton_Click(object sender, RoutedEventArgs e) => PickChoice(TextSizeButton, TextSizes, _settingsLargeChatText, large =>
    {
        _settingsLargeChatText = large;
        ShowChoice(TextSizeButton, TextSizes, large);
    });

    void LanguageButton_Click(object sender, RoutedEventArgs e) => PickChoice(LanguageButton, Languages, _settingsRussian, russian =>
    {
        _settingsRussian = russian;
        ShowChoice(LanguageButton, Languages, russian);
    });

    void PreviewTheme(string? theme)
    {
        _settingsTheme = theme;
        ShowChoice(ThemeButton, Themes, theme);
        // Preview only: state.json keeps the saved theme until «Сохранить».
        var saved = _state.Theme;
        _state.Theme = _settingsTheme;
        ApplyTheme();
        _state.Theme = saved;
    }

    void GroqKeysLink_Click(object sender, RoutedEventArgs e) => FileActions.OpenUrl(Groq.KeysUrl);

    void OpenPlugins_Click(object sender, RoutedEventArgs e)
    {
        var folder = _toolContext.PluginsFolder!;
        try
        {
            Directory.CreateDirectory(folder);
            FileActions.OpenFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowToast(Tr("Не удалось создать папку плагинов: ", "Could not create the plugins folder: ") + ex.Message, ToastKind.Error); }
    }
}
