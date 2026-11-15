using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Tools;
using SharpAgent.Ui;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    private enum ToastKind { Info, Success, Warning, Error }

    private sealed record BannerInfo(string Glyph, string Title, string Text, string ActionText, Action Action, bool Warning);

    private readonly Dictionary<string, BannerInfo> _banners = new();
    private readonly Dictionary<AgentProvider, string> _settingsKeys = new();
    private readonly Dictionary<AgentProvider, string> _settingsModels = new();
    private readonly Dictionary<AgentProvider, string> _savedModels = new();
    private AgentProvider _settingsProvider;
    private CancellationTokenSource? _signIn;
    private DispatcherTimer? _toastTimer;
    private Action? _toastAction;
    private int _toastVersion;

    private void ShowToast(string text, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
    {
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
        ToastAction.Visibility = action != null && actionText != null ? Visibility.Visible : Visibility.Collapsed;
        ToastHost.IsHitTestVisible = action != null;
        AutomationProperties.SetLiveSetting(ToastText,
            kind == ToastKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);

        bool wasHidden = ToastHost.Visibility != Visibility.Visible;
        ToastHost.Visibility = Visibility.Visible;
        if (Theme.AnimationsEnabled)
            ToastHost.BeginAnimation(OpacityProperty, wasHidden
                ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                : new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
        else
        {
            ToastHost.BeginAnimation(OpacityProperty, null);
            ToastHost.Opacity = 1;
        }

        if (_toastTimer == null)
        {
            _toastTimer = new DispatcherTimer();
            _toastTimer.Tick += (_, _) => HideToast();
        }
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
        fade.Completed += (_, _) =>
        {
            if (version == _toastVersion) ToastHost.Visibility = Visibility.Collapsed;
        };
        ToastHost.BeginAnimation(OpacityProperty, fade);
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        HideToast();
        action?.Invoke();
    }

    private void ShowBanner(string key, string glyph, string title, string text, string actionText, Action action,
        bool warning = false)
    {
        _banners[key] = new BannerInfo(glyph, title, text, actionText, action, warning);
        RenderBanner();
    }

    private void HideBanner(string key)
    {
        if (_banners.Remove(key)) RenderBanner();
    }

    private BannerInfo? TopBanner => _banners.Values.OrderByDescending(b => b.Warning).FirstOrDefault();

    private void RenderBanner()
    {
        if (TopBanner is not { } banner)
        {
            Banner.Visibility = Visibility.Collapsed;
            return;
        }

        BannerIcon.Text = banner.Glyph;
        BannerIcon.Foreground = banner.Warning ? Theme.Warning : Theme.AccentText;
        BannerTitle.Text = banner.Title;
        BannerText.Text = banner.Text;
        BannerAction.Content = banner.ActionText;
        Banner.Background = banner.Warning ? Theme.WarningSoft : Theme.Get("BannerBg");
        Banner.BorderBrush = banner.Warning ? Theme.WarningLine : Theme.AccentLine;
        Banner.Visibility = Visibility.Visible;
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
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        CloseFlyout();
        CloseOverlay();
        SettingsError.Visibility = Visibility.Collapsed;

        Dictionary<string, string> env = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(AppPaths.EnvPath)) env = DotEnv.Parse(AppPaths.EnvPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowSettingsError("Не удалось прочитать .env: " + ex.Message);
        }
        string Value(string key, string fallback = "") => env.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

        _settingsKeys[AgentProvider.Tooken] = AppConfig.CleanKey(Value("TOOKEN_API_KEY"));
        _settingsKeys[AgentProvider.Groq] = AppConfig.CleanKey(Value("GROQ_API_KEY"));
        foreach (var provider in Enum.GetValues<AgentProvider>())
            _settingsModels[provider] = _savedModels[provider] = provider == _config.Provider
                ? CurrentModel()
                : Value(AppConfig.KeyOf(provider, "MODEL"), AppConfig.DefaultModelOf(provider));
        ImageModelButton.Content = Value("TOOKEN_IMAGE_MODEL", AppConfig.DefaultImageModel);
        ImageModelButton.Tag = _imageModels ?? [(string)ImageModelButton.Content];
        SubagentsToggle.IsChecked = _config.UseSubagents;
        BaseUrlBox.Text = Value("TOOKEN_BASE_URL", AppConfig.DefaultBaseUrl);
        var plugins = PluginTools.Load(_toolContext.PluginsFolder);
        PluginsText.Text = plugins.Count > 0
            ? $"Установлены: {string.Join(", ", plugins.Select(p => p.Name))}. Агент сам выбирает подходящий плагин по задаче."
            : "Плагинов нет. Плагин — это папка с файлом PLUGIN.md внутри папки plugins.";
        MaxTokensBox.Text = Value("TOOKEN_MAX_TOKENS");
        DayLimitBox.Text = _config.DayLimit is { } day ? Tokens.ToMillions(day) : "";
        MonthLimitBox.Text = _config.MonthLimit is { } month ? Tokens.ToMillions(month) : "";
        foreach (var field in new Control[] { BaseUrlBox, MaxTokensBox, DayLimitBox, MonthLimitBox })
            field.Tag = null;

        _settingsProvider = _config.Provider;
        ProviderPill(_settingsProvider).IsChecked = true;
        ShowProviderSettings();

        SettingsOverlay.Visibility = Visibility.Visible;
        if (Theme.AnimationsEnabled)
            SettingsOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        Dispatcher.InvokeAsync(() =>
        {
            if (focus != null)
            {
                focus.BringIntoView();
                focus.Focus();
            }
            else
            {
                SettingsScroll.ScrollToTop();
                if (_settingsProvider == AgentProvider.ChatGpt && !_chatGpt.PlanAllowed) ChatGptSignInButton.Focus();
                else if (_settingsProvider != AgentProvider.ChatGpt && KeyPasswordBox.Password.Length == 0) KeyPasswordBox.Focus();
                else DefaultModelButton.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private RadioButton ProviderPill(AgentProvider provider) => provider switch
    {
        AgentProvider.ChatGpt => ChatGptPill,
        AgentProvider.Groq => GroqPill,
        _ => TookenPill,
    };

    private void ProviderPill_Checked(object sender, RoutedEventArgs e)
    {
        var provider = ReferenceEquals(sender, ChatGptPill) ? AgentProvider.ChatGpt
            : ReferenceEquals(sender, GroqPill) ? AgentProvider.Groq
            : AgentProvider.Tooken;
        if (provider == _settingsProvider) return;
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
        KeyPanel.Visibility = keyed ? Visibility.Visible : Visibility.Collapsed;
        ChatGptPanel.Visibility = keyed ? Visibility.Collapsed : Visibility.Visible;
        TookenPanel.Visibility = provider == AgentProvider.Tooken ? Visibility.Visible : Visibility.Collapsed;
        if (keyed)
        {
            ShowKeyToggle.IsChecked = false;
            KeyPasswordBox.Password = _settingsKeys.GetValueOrDefault(provider, "");
            KeyTextBox.Text = KeyPasswordBox.Password;
            KeyLabel.Text = groq ? "API-ключ Groq" : "API-ключ tooken.club";
            KeyHint.Text = groq
                ? "Ключ создаётся в консоли Groq и начинается с gsk_. "
                : "Ключ выдаётся в личном кабинете и начинается с tc_live_. ";
            KeyLinkText.Text = groq ? "Открыть страницу ключей" : "Открыть кабинет";
            AutomationProperties.SetName(KeyPasswordBox, KeyLabel.Text);
            AutomationProperties.SetName(KeyTextBox, KeyLabel.Text);
        }
        else
        {
            ShowChatGptAccount();
        }
        VoiceKeyPanel.Visibility = groq ? Visibility.Collapsed : Visibility.Visible;
        if (!groq) VoiceKeyBox.Password = _settingsKeys.GetValueOrDefault(AgentProvider.Groq, "");
        VoiceHint.Text = groq
            ? "Микрофон в поле ввода отправляет запись в Groq по ключу из раздела «Подключение» и вставляет распознанный текст. " +
              "Запись нигде не сохраняется. "
            : "Микрофон в поле ввода отправляет запись в Groq и вставляет распознанный текст — при любом провайдере. " +
              "Запись нигде не сохраняется. Бесплатный ключ создаётся в консоли Groq и начинается с gsk_. ";
        DefaultModelButton.Content = _settingsModels.GetValueOrDefault(provider) is { Length: > 0 } model
            ? model
            : AppConfig.DefaultModelOf(provider);
        DefaultModelButton.Tag = _modelLists.GetValueOrDefault(provider) ?? (IEnumerable<string>)DefaultModelsOf(provider);
        SetTestResult("", null);
    }

    private void ShowChatGptAccount()
    {
        bool connected = _chatGpt.Connected, allowed = _chatGpt.PlanAllowed, waiting = _signIn != null;
        ChatGptAccountPanel.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ChatGptSignInPanel.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
        ChatGptEmail.Text = _chatGpt.Label ?? "Аккаунт ChatGPT";
        ChatGptPlan.Text = allowed ? "Подписка ChatGPT подключена" : "Использование подписки не разрешено";
        ChatGptSignInButton.IsEnabled = !waiting;
        ChatGptSignInButton.Content = waiting ? "Ожидаю вход в браузере…" : connected ? "Разрешить использование подписки" : "Продолжить с ChatGPT";
        ChatGptSignInButton.ToolTip = connected
            ? "Открыть сайт OpenAI и разрешить Sharp Agent использовать подписку ChatGPT"
            : "Открыть сайт OpenAI, войти в ChatGPT и подключить подписку к Sharp Agent";
        AutomationProperties.SetName(ChatGptSignInButton, (string)ChatGptSignInButton.Content);
        ChatGptCancelButton.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        ChatGptHint.Text = waiting
            ? "В браузере открылась страница OpenAI. Войдите и разрешите Sharp Agent использовать подписку — программа ждёт до 10 минут."
            : allowed
                ? "Запросы агента расходуют вашу подписку ChatGPT."
                : connected
                    ? "Вы вошли, но не разрешили Sharp Agent использовать подписку. Нажмите кнопку и разрешите это на сайте OpenAI " +
                      "или выберите другого провайдера — Tooken Club или Groq."
                    : _chatGpt.Label is { } label
                        ? $"Sharp Agent подключён к {label}: нажмите «Продолжить с ChatGPT», чтобы войти снова."
                        : "Откроется сайт OpenAI. Войдите в ChatGPT и разрешите Sharp Agent использовать подписку.";
        if (waiting || connected) return;
        foreach (var account in _chatGpt.SavedAccounts)
            AddSignInLink("Войти как " + (account.Label ?? "аккаунт ChatGPT"), "Войти в сохранённый аккаунт ChatGPT", account.ClientId);
        if (_chatGpt.ClientId != null) AddSignInLink("Войти в другой аккаунт", "Подключить к Sharp Agent другой аккаунт ChatGPT", null);
    }

    private void AddSignInLink(string text, string tip, string? clientId)
    {
        var link = new Hyperlink(new Run(text)) { ToolTip = tip };
        link.Click += async (_, _) => await SignInChatGptAsync(clientId);
        ChatGptHint.Inlines.Add(new LineBreak());
        ChatGptHint.Inlines.Add(link);
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
            if (SettingsOverlay.Visibility != Visibility.Visible)
                ShowToast(_chatGpt.PlanAllowed
                    ? "Подписка ChatGPT подключена" + (_chatGpt.Label is { } label ? ": " + label : "")
                    : "Вход выполнен, но Sharp Agent не получил разрешения использовать подписку ChatGPT",
                    _chatGpt.PlanAllowed ? ToastKind.Success : ToastKind.Warning);
            else if (_settingsProvider == AgentProvider.ChatGpt && _chatGpt.PlanAllowed)
                TestKey_Click(this, new RoutedEventArgs());
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is ApiException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            var text = ex is ApiException ? ex.Message : "Не удалось войти в ChatGPT: " + ex.Message + " Попробуйте ещё раз.";
            if (SettingsOverlay.Visibility == Visibility.Visible) SetTestResult(text, false);
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
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new ApiException("Не удалось открыть браузер для входа в ChatGPT. Выберите браузер по умолчанию в параметрах Windows " +
                                   "и нажмите «Продолжить с ChatGPT» ещё раз.");
        }
    }

    private void ChatGptCancel_Click(object sender, RoutedEventArgs e) => _signIn?.Cancel();

    private async void ChatGptSignOut_Click(object sender, RoutedEventArgs e)
    {
        if (!DarkDialog.Ask(this, "Выйти из ChatGPT?",
                "Чтобы снова работать через ChatGPT, нажмите «Продолжить с ChatGPT».", "Выйти"))
            return;
        ChatGptSignOutButton.IsEnabled = false;
        bool revoked;
        try
        {
            revoked = await _chatGpt.SignOutAsync();
        }
        catch (ApiException ex)
        {
            ShowToast(ex.Message, ToastKind.Error);
            return;
        }
        finally
        {
            ChatGptSignOutButton.IsEnabled = true;
        }
        _modelLists.Remove(AgentProvider.ChatGpt);
        _state.ModelCatalogs?.Remove(AgentProvider.ChatGpt);
        _state.Save();
        if (_config.Provider == AgentProvider.ChatGpt) ApplyProvider();
        if (SettingsOverlay.Visibility == Visibility.Visible) ShowProviderSettings();
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
        KeyTextBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        KeyPasswordBox.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(ShowKeyToggle, show ? "Скрыть ключ" : "Показать ключ");
    }

    private async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var provider = _settingsProvider;
        IChatProvider client;
        if (provider == AgentProvider.ChatGpt)
        {
            if (!_chatGpt.PlanAllowed)
            {
                SetTestResult(_chatGpt.Connected
                    ? "Использование подписки не разрешено: нажмите «Разрешить использование подписки» " +
                      "или выберите другого провайдера — Tooken Club или Groq."
                    : "Сначала войдите: нажмите «Продолжить с ChatGPT».", false);
                return;
            }
            client = new ChatGptClient(_chatGpt);
        }
        else
        {
            var key = EnteredKey();
            if (key.Length == 0)
            {
                SetTestResult("Сначала вставьте ключ.", false);
                return;
            }
            var url = BaseUrlBox.Text.Trim().Length > 0 ? BaseUrlBox.Text.Trim() : AppConfig.DefaultBaseUrl;
            client = provider == AgentProvider.Groq
                ? new ChatApiClient(Groq.BaseUrl, key, AgentProvider.Groq)
                : new ChatApiClient(AppConfig.NormalizeBaseUrl(url), key);
        }

        TestKeyButton.IsEnabled = false;
        SetTestResult("Проверяю…", null);
        try
        {
            using (client)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var models = await client.ListModelsAsync(cts.Token);
                RememberModelList(provider, models);
                if (provider != _settingsProvider) return;
                var count = models.Count(m => m.Chat);
                SetTestResult((provider == AgentProvider.ChatGpt ? "Вход выполнен." : "Ключ принят.") +
                              (count > 0 ? $" Доступно моделей: {count}." : ""), true);
                if (count > 0) DefaultModelButton.Tag = _modelLists[provider];
                if (provider == AgentProvider.Tooken && _imageModels is { Count: > 0 }) ImageModelButton.Tag = _imageModels;
            }
        }
        catch (ApiException ex)
        {
            SetTestResult(ex.Message, false);
        }
        catch (OperationCanceledException)
        {
            SetTestResult("Сервер не ответил за 20 секунд. Проверьте адрес API и подключение к интернету.", false);
        }
        catch (Exception ex)
        {
            SetTestResult("Не удалось подключиться: " + ex.Message, false);
        }
        finally
        {
            TestKeyButton.IsEnabled = true;
            if (provider == AgentProvider.ChatGpt) ShowChatGptAccount();
            if (_provider is ChatGptClient && !_chatGpt.PlanAllowed && _cts == null) ApplyProvider();
        }
    }

    private void DefaultModelButton_Click(object sender, RoutedEventArgs e) =>
        PickModel(DefaultModelButton, _settingsProvider, model => DefaultModelButton.Content = model);

    private void ImageModelButton_Click(object sender, RoutedEventArgs e) =>
        PickModel(ImageModelButton, AgentProvider.Tooken, model => ImageModelButton.Content = model);

    private void SetTestResult(string text, bool? ok)
    {
        TestKeyResult.Text = text;
        TestKeyResult.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TestKeyResult.Foreground = ok switch { true => Theme.Success, false => Theme.DangerText, _ => Theme.TextMuted };
        TestKeyIcon.Text = ok switch { true => Glyphs.Check, false => Glyphs.Warning, _ => "" };
        TestKeyIcon.Foreground = ok == true ? Theme.Success : Theme.DangerText;
        AutomationProperties.SetLiveSetting(TestKeyResult, AutomationLiveSetting.Polite);
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        Control? invalid = null;
        void Fail(string message, Control field)
        {
            field.Tag = "invalid";
            if (invalid != null) return;
            invalid = field;
            ShowSettingsError(message);
        }

        var maxTokens = MaxTokensBox.Text.Trim();
        if (maxTokens.Length > 0 && (!int.TryParse(maxTokens, out var limit) || limit < 1))
            Fail("«Токенов в одном ответе» — целое число больше нуля или пустое поле.", MaxTokensBox);

        var url = BaseUrlBox.Text.Trim().Length > 0 ? BaseUrlBox.Text.Trim() : AppConfig.DefaultBaseUrl;
        if (!(Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
              (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
            Fail("Адрес API tooken.club должен начинаться с https://" +
                 (_settingsProvider == AgentProvider.Tooken ? "" : " — исправьте его в разделе Tooken Club."), BaseUrlBox);

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
        var imageModel = ImageModelButton.Content as string ?? "";
        var values = new Dictionary<string, string?>
        {
            ["AGENT_PROVIDER"] = _settingsProvider.ToString().ToLowerInvariant(),
            ["AGENT_USE_SUBAGENTS"] = SubagentsToggle.IsChecked == true ? "true" : "false",
            ["TOOKEN_API_KEY"] = _settingsKeys.GetValueOrDefault(AgentProvider.Tooken, ""),
            ["GROQ_API_KEY"] = _settingsKeys.GetValueOrDefault(AgentProvider.Groq, ""),
            ["TOOKEN_BASE_URL"] = url,
            ["TOOKEN_IMAGE_MODEL"] = imageModel.Length > 0 ? imageModel : AppConfig.DefaultImageModel,
            ["TOOKEN_MAX_TOKENS"] = maxTokens,
            ["AGENT_DAY_LIMIT_MLN"] = dayLimit.Replace(',', '.'),
            ["AGENT_MONTH_LIMIT_MLN"] = monthLimit.Replace(',', '.'),
        };
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
        if (_provider != null) ShowToast("Настройки сохранены", ToastKind.Success);
        else
            ShowToast(_config.Provider switch
            {
                AgentProvider.ChatGpt => "Сохранено, но подписка ChatGPT не подключена — агент не сможет отвечать",
                AgentProvider.Groq => "Сохранено, но API-ключ Groq пуст — агент не сможет отвечать",
                _ => "Сохранено, но API-ключ пуст — агент не сможет отвечать",
            }, ToastKind.Warning);
    }

    private void ShowSettingsError(string message)
    {
        SettingsError.Text = message;
        SettingsError.Visibility = Visibility.Visible;
        AutomationProperties.SetLiveSetting(SettingsError, AutomationLiveSetting.Assertive);
    }

    private void KeyLink_Click(object sender, RoutedEventArgs e) =>
        FileActions.OpenUrl(_settingsProvider == AgentProvider.Groq ? Groq.KeysUrl : "https://tooken.club/dashboard");

    private void GroqKeysLink_Click(object sender, RoutedEventArgs e) => FileActions.OpenUrl(Groq.KeysUrl);

    private void OpenPlugins_Click(object sender, RoutedEventArgs e)
    {
        var folder = _toolContext.PluginsFolder!;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast("Не удалось создать папку плагинов: " + ex.Message, ToastKind.Error);
            return;
        }
        FileActions.OpenFolder(folder);
    }
}
