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
    static readonly (string? Value, string Label)[] Themes = [(null, "Тёмная"), ("light", "Светлая"), ("system", "Как в Windows")];
    static readonly (bool Value, string Label)[] ImageSources = [(false, "Tooken Club"), (true, "Как в чате")];
    bool _settingsImagesLikeChat;
    string? _settingsTheme;
    FrameworkElement? _settingsPage;
    CancellationTokenSource? _signIn;
    DispatcherTimer? _toastTimer;
    Action? _toastAction;


    static Hyperlink MakeLink(string text, Action click, string? tip = null)
    {
        var link = new Hyperlink(new Run(text)) { ToolTip = tip };
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

    Control KeyField => _config.Provider == AgentProvider.ChatGpt ? ChatGptSignInButton : KeyPasswordBox;

    void OpenSettings(Control? focus)
    {
        if (_cts != null)
        {
            ShowToast("Остановите агента, чтобы изменить настройки", ToastKind.Warning);
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
            ShowSettingsError("Не удалось прочитать .env: " + ex.Message);
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
        ComputerUseToggle.IsChecked = _config.ComputerUse;
        _settingsTheme = _state.Theme;
        ShowChoice(ThemeButton, Themes, _settingsTheme);
        BaseUrlBox.Text = Value("TOOKEN_BASE_URL", AppConfig.DefaultBaseUrl);
        LoadNetworkSettings(unreadable ? _config.Network : NetworkSettings.Load(key => env.GetValueOrDefault(key)), Value("AGENT_VPN_PROFILES"));
        var plugins = PluginTools.Load(_toolContext.PluginsFolder);
        PluginsText.Text = plugins.Count > 0
            ? $"Установлены: {string.Join(", ", plugins.Select(p => p.Name))}. Агент сам выбирает подходящий плагин по задаче."
            : "Плагинов нет. Плагин — это папка с файлом PLUGIN.md внутри папки plugins.";
        MaxTokensBox.Text = Value("TOOKEN_MAX_TOKENS");
        DayLimitBox.Text = _config.DayLimit is { } day ? Tokens.ToMillions(day) : "";
        MonthLimitBox.Text = _config.MonthLimit is { } month ? Tokens.ToMillions(month) : "";
        ClearInvalidFields();
        _settingsProvider = _config.Provider;
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
        PickChoice(ProviderButton, Enum.GetValues<AgentProvider>().Select(provider => (provider, ProviderName(provider))).ToArray(), _settingsProvider, provider =>
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
        TestKeyButton.Content = keyed ? "Проверить API-ключ" : "Проверить вход";
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
            _ => "https://tooken.club/dashboard",
        });
        if (keyed)
        {
            ShowKeyToggle.IsChecked = false;
            KeyTextBox.Text = KeyPasswordBox.Password = _settingsKeys.GetValueOrDefault(provider, "");
            KeyLabel.Text = "API-ключ " + ProviderName(provider);
            KeyHint.Text = provider switch
            {
                AgentProvider.Groq => "Ключ создаётся в консоли Groq и начинается с gsk_.",
                AgentProvider.Tooken => "Ключ выдаётся в личном кабинете и начинается с tc_live_.",
                _ => "Создайте API-ключ в кабинете провайдера.",
            };
            AutomationProperties.SetName(KeyPasswordBox, KeyLabel.Text);
            AutomationProperties.SetName(KeyTextBox, KeyLabel.Text);
        }
        else ShowChatGptAccount();
        VoiceKeyPanel.Visibility = VisibleIf(!groq);
        if (!groq) VoiceKeyBox.Password = _settingsKeys.GetValueOrDefault(AgentProvider.Groq, "");
        VoiceHint.Text = groq
            ? "Микрофон в поле ввода отправляет запись в Groq по API-ключу выше и вставляет распознанный текст. Запись нигде не сохраняется. "
            : "Микрофон в поле ввода отправляет запись в Groq и вставляет распознанный текст — при любом провайдере. Запись нигде не сохраняется. " +
              "Бесплатный ключ создаётся в консоли Groq и начинается с gsk_. ";
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
        ImageModelHint.Text = target != AgentProvider.Tooken ? $"Картинки рисует {ProviderName(target)} по ключу из этого раздела."
            : _settingsProvider == AgentProvider.Tooken ? "Картинки рисует Tooken Club по его ключу и балансу."
            : _settingsImagesLikeChat
                ? $"{ProviderName(_settingsProvider)} не рисует картинки через API, поэтому их рисует Tooken Club по его ключу и балансу."
                : "Картинки рисует Tooken Club по его ключу и балансу при любом провайдере чата.";
    }

    void ImageSourceButton_Click(object sender, RoutedEventArgs e) => PickChoice(ImageSourceButton, ImageSources, _settingsImagesLikeChat, likeChat =>
    {
        _settingsImagesLikeChat = likeChat;
        ShowChoice(ImageSourceButton, ImageSources, likeChat);
        ShowImageSettings();
    });

    void ShowChatGptAccount()
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

    void AddSignInLink(string text, string tip, string? clientId)
    {
        ChatGptHint.Inlines.Add(new LineBreak());
        ChatGptHint.Inlines.Add(MakeLink(text, async () => await SignInChatGptAsync(clientId), tip));
    }

    async void ChatGptSignIn_Click(object sender, RoutedEventArgs e) => await SignInChatGptAsync(_chatGpt.ClientId);

    async Task SignInChatGptAsync(string? clientId)
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
            if (!SettingsOpen)
                ShowToast(allowed ? "Подписка ChatGPT подключена" + (_chatGpt.Label is { } label ? ": " + label : "")
                    : "Вход выполнен, но Sharp Agent не получил разрешения использовать подписку ChatGPT", allowed ? ToastKind.Success : ToastKind.Warning);
            else if (_settingsProvider == AgentProvider.ChatGpt && allowed) TestKey_Click(this, new RoutedEventArgs());
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ApiException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            var text = ex is ApiException ? ex.Message : "Не удалось войти в ChatGPT: " + ex.Message + " Попробуйте ещё раз.";
            if (SettingsOpen) SetTestResult(text, false);
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

    static void OpenSignInPage(string url)
    {
        if (AppNetwork.CustomRoute) FileActions.RaiseNotify("Для входа откроется внешний браузер с его сетевыми настройками.", false);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new ApiException("Не удалось открыть браузер для входа в ChatGPT. Выберите браузер по умолчанию в параметрах Windows и нажмите «Продолжить с ChatGPT» ещё раз.");
        }
    }

    void ChatGptCancel_Click(object sender, RoutedEventArgs e) => _signIn?.Cancel();

    async void ChatGptSignOut_Click(object sender, RoutedEventArgs e)
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
        if (SettingsOpen)
        {
            KeepProviderInput();
            ShowProviderSettings();
        }
        if (!revoked)
            ShowToast("Вход удалён из программы, но OpenAI не подтвердил его отзыв. Отключить Sharp Agent можно в настройках ChatGPT.",
                ToastKind.Warning, "Открыть", () => FileActions.OpenUrl(ChatGptClient.UsageUrl));
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
        AutomationProperties.SetName(ShowKeyToggle, show ? "Скрыть ключ" : "Показать ключ");
    }

    async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var provider = _settingsProvider;
        var key = EnteredKey();
        var baseUrl = SettingsTookenBaseUrl();
        var connection = ModelCatalogKey(provider, key, baseUrl);
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
            AgentProvider.Tooken => new ChatApiClient(baseUrl, key),
            _ => new ChatApiClient(AppConfig.DefaultBaseUrlOf(provider), key, provider),
        };
        TestKeyButton.IsEnabled = false;
        SetTestResult("Проверяю…", null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (provider == AgentProvider.OpenRouter && client is ChatApiClient router) await router.CheckOpenRouterKeyAsync(cts.Token);
            var models = await client.ListModelsAsync(cts.Token);
            if (provider != _settingsProvider || !SettingsOpen) return;
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
            SetTestResult((provider == AgentProvider.ChatGpt ? "Вход выполнен." : "Ключ принят.") +
                          (count > 0 ? $" Доступно моделей: {count}. Нажмите на название модели, чтобы открыть список." : ""), true);
            if (provider == AgentProvider.Tooken) ShowImageSettings();
        }
        catch (Exception ex)
        {
            if (provider != _settingsProvider || !SettingsOpen || SettingsCatalogKey() != connection) return;
            SetTestResult(ex switch
            {
                ApiException { StatusCode: HttpStatusCode.Unauthorized } => provider == AgentProvider.ChatGpt
                    ? "Провайдер отклонил авторизацию (HTTP 401). Войдите в ChatGPT заново."
                    : "Провайдер отклонил авторизацию (HTTP 401). Проверьте API-ключ и выбранного провайдера.",
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
            Fail("«Токенов в одном ответе» — целое число больше нуля или пустое поле.", MaxTokensBox);
        var url = BaseUrlBox.Text.Trim() is { Length: > 0 } text ? text : AppConfig.DefaultBaseUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            Fail("Адрес API tooken.club должен начинаться с https://" + (_settingsProvider == AgentProvider.Tooken ? "" : " — выберите Tooken Club в разделе «Провайдеры» и исправьте адрес."), BaseUrlBox);
        var dayLimit = DayLimitBox.Text.Trim();
        if (dayLimit.Length > 0 && AppConfig.ParseMillions(dayLimit) == null)
            Fail("«Лимит в день» — число миллионов токенов, например 5 или 0,5. Чтобы снять лимит, оставьте поле пустым.", DayLimitBox);
        var monthLimit = MonthLimitBox.Text.Trim();
        if (monthLimit.Length > 0 && AppConfig.ParseMillions(monthLimit) == null)
            Fail("«Лимит в месяц» — число миллионов токенов, например 50. Чтобы снять лимит, оставьте поле пустым.", MonthLimitBox);
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
        if (SaveEnv(values) is { } error)
        {
            ShowSettingsError("Не удалось сохранить файл .env: " + error);
            return;
        }
        _state.LimitOverrideDay = null;
        _state.Theme = _settingsTheme;
        _state.Save();
        ApplyConfig();
        CloseSettings();
        ShowToast(_provider != null ? "Настройки сохранены" : "Сохранено, но " + (_config.Provider switch
        {
            AgentProvider.ChatGpt => "подписка ChatGPT не подключена",
            AgentProvider.Groq => "API-ключ Groq пуст",
            _ => "API-ключ пуст",
        }) + " — агент не сможет отвечать", _provider != null ? ToastKind.Success : ToastKind.Warning);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowToast("Не удалось создать папку плагинов: " + ex.Message, ToastKind.Error); }
    }
}
