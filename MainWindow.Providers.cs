using System.Windows.Documents;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    private readonly Dictionary<AgentProvider, Dictionary<string, string>> _modelNames = new();
    readonly Dictionary<(AgentProvider Provider, string Connection), Task<List<ModelInfo>>> _modelCatalogRequests = [];
    private string? _selectedModel;
    private bool _loadingModelCatalog;
    (AgentRunner Chat, (AgentProvider Provider, string? Model, string? Name) Previous)? _pendingModelChange;

    private static string ProviderName(AgentProvider provider) => provider switch
    {
        AgentProvider.ChatGpt => "ChatGPT", AgentProvider.OpenAI => "OpenAI API", AgentProvider.Groq => "Groq", AgentProvider.Gemini => "Google Gemini",
        AgentProvider.OpenRouter => "OpenRouter", AgentProvider.Mistral => "Mistral AI", AgentProvider.Anthropic => "Claude API", _ => "Tooken Club",
    };

    static IEnumerable<AgentProvider> ProvidersByName() => Enum.GetValues<AgentProvider>().OrderBy(ProviderName, StringComparer.OrdinalIgnoreCase);

    private bool IsReady(AgentProvider provider) =>
        provider == AgentProvider.ChatGpt ? _chatGpt.PlanAllowed : _config.ApiKeyFor(provider) != null;

    private string NotReadyText() => _config.Provider switch
    {
        AgentProvider.ChatGpt when _chatGpt.Connected => Tr("Разрешите Sharp Agent использовать подписку ChatGPT в настройках", "Allow Sharp Agent to use your ChatGPT subscription in settings"),
        AgentProvider.ChatGpt => Tr("Сначала войдите в ChatGPT в настройках", "Sign in to ChatGPT in settings first"),
        _ => Tr("Сначала укажите API-ключ в настройках", "Enter an API key in settings first"),
    };

    IChatProvider? CreateProvider(AgentProvider provider, string? key, string tookenBaseUrl) => provider switch
    {
        AgentProvider.ChatGpt => _chatGpt.PlanAllowed ? new ChatGptClient(_chatGpt) : null,
        _ when string.IsNullOrEmpty(key) => null,
        AgentProvider.OpenAI => new OpenAiResponsesClient(key),
        AgentProvider.Anthropic => new AnthropicClient(key),
        AgentProvider.Tooken => new ChatApiClient(tookenBaseUrl, key),
        _ => new ChatApiClient(AppConfig.DefaultBaseUrlOf(provider), key, provider),
    };

    private void ApplyProvider()
    {
        if (AnyRunning) return;
        var provider = _config.Provider;
        _pendingModelChange = null;
        if (!ReferenceEquals(_provider, _tooken)) _provider?.Dispose();
        _tooken?.Dispose();
        _tooken = _config.HasApiKey ? new ChatApiClient(_config.BaseUrl, _config.ApiKey!) : null;
        _provider = provider == AgentProvider.Tooken ? _tooken : CreateProvider(provider, _config.ApiKeyFor(provider), _config.BaseUrl);
        if (_provider is ChatGptClient chat) chat.LimitsChanged += OnLimitsChanged;
        else if (provider == AgentProvider.Groq && _provider is ChatApiClient groq) groq.LimitsChanged += OnLimitsChanged;
        _agent.Api = _provider;
        _agent.Provider = provider;
        var drawer = _config.ImageProvider;
        _toolContext.GenerateImage = drawer != AgentProvider.Tooken
            ? (prompt, size, ct) => ImageApi.GenerateAsync(drawer, _config.ApiKeyFor(drawer)!, _config.ImageModelOf(drawer), prompt, size, ct)
            : _tooken is { } images ? (prompt, size, ct) => images.GenerateImageAsync(_config.ImageModelOf(drawer), prompt, size, ct) : null;
        _models = new(StringComparer.OrdinalIgnoreCase);
        _modelLists.Remove(provider);
        _modelNames.Remove(provider);
        var cached = CachedModelCatalog(provider, ModelCatalogKey(provider));
        if (cached != null) ApplyModelCatalog(provider, cached.Models);
        else
        {
            if (provider == AgentProvider.Tooken) _imageModels = null;
            ModelButton.Tag = null;
        }
        _loadingModelCatalog = false;
        _selectedModel = _config.Model;
        ModelButton.ToolTip = ProviderName(provider) + (_provider is ChatGptClient ? "\nЗапросы расходуют вашу подписку ChatGPT" : "");
        UpdateModelEffort();
        HideBanner("plan");
        if (_provider != null) HideBanner("key");
        else
        {
            var (title, text, action) = provider != AgentProvider.ChatGpt
                ? (Tr("Нужен API-ключ", "API key required"), Tr("Введите API-ключ, чтобы агент мог отвечать.", "Enter an API key so the agent can respond."), Tr("Указать ключ", "Enter API key"))
                : _chatGpt.Connected ? (Tr("Подписка ChatGPT не подключена", "ChatGPT subscription not connected"),
                    Tr("Разрешите Sharp Agent использовать подписку в настройках или выберите другого провайдера.", "Allow subscription use in settings or choose another provider."), Tr("Разрешить", "Allow"))
                : (Tr("Нужен вход в ChatGPT", "ChatGPT sign-in required"), Tr("Войдите на сайте OpenAI и разрешите Sharp Agent использовать подписку.", "Sign in on the OpenAI website and allow Sharp Agent to use your subscription."), Tr("Войти", "Sign in"));
            ShowBanner("key", Glyphs.Warning, title, text, action, ShowSettings);
        }
        _usage.Reset();
        _usageUnavailable = false;
        _usageError = null;
        UpdateBalanceIndicator();
        if (_provider is not ChatGptClient) _ = _chatGpt.RenewAsync();
        if (_provider == null) return;
        if (cached?.UpdatedUtc is not { } updated || DateTimeOffset.UtcNow - updated >= TimeSpan.FromDays(1))
            _ = LoadModelListAsync();
        _ = RefreshUsageAsync();
    }

    private void OnLimitsChanged()
    {
        UpdateBalanceIndicator();
        if (FlyoutShows(UsageCard)) BuildUsagePanel();
        if ((_provider as ChatGptClient)?.LimitMessage is { } limit)
            ShowBanner("plan", Glyphs.Warning, "Ограничение подписки ChatGPT", limit, Tr("Управлять использованием", "Manage usage"), () => FileActions.OpenUrl(ChatGptClient.UsageUrl), warning: true);
        else HideBanner("plan");
    }

    private void ShowPlanWelcome() => DarkDialog.Show(this, Tr("Вы используете подписку ChatGPT", "You are using a ChatGPT subscription"), new TextBlock
    {
        FontSize = 14, Foreground = Theme.TextSoft, TextWrapping = TextWrapping.Wrap,
        Inlines =
        {
            new Run(Tr("Запросы в Sharp Agent будут расходовать вашу подписку ChatGPT. Сколько из неё уходит на Sharp Agent и какие у него лимиты, смотрите и меняйте в настройках ChatGPT. ", "Sharp Agent requests count toward your ChatGPT subscription. Check your usage and limits in ChatGPT settings. ")),
            MakeLink(Tr("Управлять использованием", "Manage usage"), () => FileActions.OpenUrl(ChatGptClient.UsageUrl)),
        },
    }, closeText: Tr("Понятно", "OK"));

    async Task LoadModelListAsync(bool manual = false)
    {
        var provider = _config.Provider;
        if (_loadingModelCatalog || _provider is not { } api || ModelCatalogKey(provider) is not { } connection) return;
        _loadingModelCatalog = true;
        UpdateModelEffort();
        try
        {
            var models = await FetchModelCatalogAsync(provider, connection, _config.ApiKeyFor(provider), _config.BaseUrl);
            if (!ReferenceEquals(api, _provider)) return;
            if (models.Count == 0)
            {
                if (manual) ShowToast(Tr("Провайдер вернул пустой список моделей.", "The provider returned an empty model list."), ToastKind.Warning);
                return;
            }
            ApplyModelCatalog(provider, models);
            if (_cts == null) EnsureChatModel();
            UpdateContextBanner();
            if (CurrentModel() != null && _pendingModelChange is { } pending)
            {
                _pendingModelChange = null;
                if (pending.Chat == _agent) await ShowModelChangeAsync(pending.Previous);
            }
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (!ReferenceEquals(api, _provider)) return;
            if (provider == AgentProvider.ChatGpt && !IsReady(provider) && !AnyRunning) ApplyProvider();
            else
                ShowBanner("key", Glyphs.Warning, provider switch
                {
                    AgentProvider.ChatGpt => Tr("ChatGPT не принял вход", "ChatGPT rejected the sign-in"),
                    AgentProvider.Groq => Tr("Groq не принял API-ключ", "Groq rejected the API key"),
                    _ => Tr("Сервер не принял API-ключ", "The server rejected the API key"),
                }, ex.Message.Split('\n')[0], Tr("Открыть настройки", "Open settings"), ShowKeySettings, warning: true);
        }
        catch
        {
            if (manual && ReferenceEquals(api, _provider))
                ShowToast(Tr("Не удалось обновить список моделей. Попробуйте ещё раз.", "Could not refresh the model list. Try again."), ToastKind.Warning);
        }
        finally
        {
            if (ReferenceEquals(api, _provider))
            {
                _loadingModelCatalog = false;
                UpdateModelEffort();
            }
        }
    }

    async Task<List<ModelInfo>> FetchModelCatalogAsync(AgentProvider provider, string connection, string? key, string baseUrl)
    {
        var slot = (provider, connection);
        if (_modelCatalogRequests.TryGetValue(slot, out var request)) return await request;
        async Task<List<ModelInfo>> FetchAsync()
        {
            using var client = CreateProvider(provider, key, baseUrl);
            if (client == null) return [];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await client.ListModelsAsync(cts.Token);
            if (connection == ModelCatalogKey(provider)) CacheModelCatalog(provider, connection, models);
            return models;
        }
        _modelCatalogRequests[slot] = request = FetchAsync();
        try { return await request; }
        finally { _modelCatalogRequests.Remove(slot); }
    }

    private string? ModelCatalogKey(AgentProvider provider) =>
        ModelCatalogKey(provider, _config.ApiKeyFor(provider), _config.BaseUrl);

    private string? ModelCatalogKey(AgentProvider provider, string? key, string tookenBaseUrl) => provider switch
    {
        AgentProvider.ChatGpt when _chatGpt.PlanAllowed && _chatGpt.ClientId is { } client =>
            UsageTracker.KeyId(client, AppConfig.DefaultBaseUrlOf(AgentProvider.OpenAI) + "/models"),
        AgentProvider.ChatGpt => null,
        _ when !string.IsNullOrEmpty(key) => UsageTracker.KeyId(key, provider == AgentProvider.Tooken ? tookenBaseUrl : AppConfig.DefaultBaseUrlOf(provider)),
        _ => null,
    };

    ModelCatalogCache? CachedModelCatalog(AgentProvider provider, string? connection) =>
        connection != null && _state.ModelCatalogs?.GetValueOrDefault(provider) is { Models.Count: > 0 } cache && cache.Connection == connection
            ? cache : null;

    private void CacheModelCatalog(AgentProvider provider, string connection, List<ModelInfo> models)
    {
        if (models.Count == 0) return;
        (_state.ModelCatalogs ??= new())[provider] = new(connection, models, DateTimeOffset.UtcNow);
        _state.Save();
    }

    private void ApplyModelCatalog(AgentProvider provider, List<ModelInfo> models)
    {
        RememberModelList(provider, models);
        _models = new(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models) _models[model.Id] = model;
        if (_provider is AnthropicClient claude) claude.ApplyCatalog(_models.Values);
        ModelButton.Tag = _modelLists.GetValueOrDefault(provider);
    }

    private void RememberModelList(AgentProvider provider, List<ModelInfo> models)
    {
        if (models.Count == 0) return;
        _modelLists[provider] = models.Where(m => m.Chat && !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = _modelNames[provider] = new(StringComparer.Ordinal);
        foreach (var model in models)
            if (model.Name is { Length: > 0 } name) names[model.Id] = name;
        if (provider == AgentProvider.Tooken) _imageModels = models.Where(m => m.Images).Select(m => m.Id).ToList();
    }

    private string? CurrentModel()
    {
        if (ModelButton.Tag is not List<string> { Count: > 0 } choices) return null;
        return choices.FirstOrDefault(model => string.Equals(model, _selectedModel, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    }

    private string ModelDisplayName(string model) => _modelNames.GetValueOrDefault(_config.Provider)?.GetValueOrDefault(model) ?? model;

    (AgentProvider Provider, string? Model, string? Name) CurrentModelSelection()
    {
        var model = CurrentModel();
        return (_config.Provider, model, model == null ? null : ModelDisplayName(model));
    }

    async Task ShowModelChangeAsync((AgentProvider Provider, string? Model, string? Name) previous)
    {
        var current = CurrentModelSelection();
        bool providerChanged = previous.Provider != current.Provider;
        if (_agent.History.Count == 0 || previous.Name == null ||
            !providerChanged && string.Equals(previous.Model, current.Model, StringComparison.OrdinalIgnoreCase)) return;
        if (current.Name == null)
        {
            if (_provider != null) _pendingModelChange = (_agent, previous);
            return;
        }
        var from = providerChanged ? $"{previous.Name} ({ProviderName(previous.Provider)})" : previous.Name;
        var to = providerChanged ? $"{current.Name} ({ProviderName(current.Provider)})" : current.Name;
        ScrollToLatest();
        AddModelChange(from, to);
        await _agent.RecordModelChangeAsync(from, to);
    }

    private void ModelButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = ModelMenu(ModelButton, _config.Provider, async model => await RunChatOperationAsync(async () =>
        {
            var previous = CurrentModelSelection();
            if (_cts != null || string.Equals(_selectedModel, model, StringComparison.OrdinalIgnoreCase)) return;
            _selectedModel = model;
            SaveEnvChoice(AppConfig.KeyOf(_config.Provider, "MODEL"), model);
            UpdateModelEffort();
            UpdateContextBanner();
            await ShowModelChangeAsync(previous);
        }));
        var ready = ProvidersByName().Where(IsReady).ToList();
        if (ready.Exists(p => p != _config.Provider))
        {
            menu.Items.Insert(0, new Separator());
            foreach (var provider in Enumerable.Reverse(ready))
            {
                var item = FileActions.Item(ProviderName(provider), null, async () => await SwitchProviderAsync(provider));
                item.IsChecked = provider == _config.Provider;
                item.FontWeight = FontWeights.SemiBold;
                AutomationProperties.SetName(item, Tr("Провайдер ", "Provider ") + ProviderName(provider));
                menu.Items.Insert(0, item);
            }
        }
        if (_provider is ChatGptClient)
        {
            var note = FileActions.Item(Tr("Используется подписка ChatGPT", "Using your ChatGPT subscription"), Glyphs.Info, () => { });
            note.IsEnabled = false;
            menu.Items.Add(new Separator());
            menu.Items.Add(note);
            menu.Items.Add(FileActions.Item(Tr("Управлять использованием", "Manage usage"), Glyphs.Open, () => FileActions.OpenUrl(ChatGptClient.UsageUrl)));
        }
        if (_models.Count > 1)
        {
            var current = _state.EconomyModels?.GetValueOrDefault(_config.Provider);
            menu.Items.Add(FileActions.Item(current == null ? Tr("Модель для простых запросов…", "Model for simple requests…") :
                Tr($"Модель для простых запросов · {ModelDisplayName(current)}", $"Simple requests · {ModelDisplayName(current)}"),
                Glyphs.Lightning, () => Dispatcher.InvokeAsync(OpenEconomyModelMenu)));
        }
        if (_provider != null)
        {
            var refresh = FileActions.Item(_loadingModelCatalog ? Tr("Загрузка моделей…", "Loading models…")
                : Tr("Обновить список моделей", "Refresh model list"), Glyphs.Refresh, async () => await LoadModelListAsync(manual: true));
            refresh.IsEnabled = !_loadingModelCatalog;
            menu.Items.Add(refresh);
        }
        if (EffortLevelsOf(CurrentModel()) is { Length: > 0 } levels)
        {
            var divider = new Separator();
            var label = new MenuItem { Header = new TextBlock { Style = Theme.Style("MenuLabel"), Text = Tr("Выбрать модель", "Choose a model") }, Style = Theme.Style("EffortHeader"), Margin = new(6, 0, 6, 0) };
            var list = menu.Items.OfType<FrameworkElement>().ToList();
            // The popover template has no padding, so the list keeps the menu inset itself.
            foreach (var item in list) item.Margin = new(6, 0, 6, 0);
            if (list.Count > 0) list[^1].Margin = new(6, 0, 6, 6);
            var hidden = list.Append(divider).Append(label).ToArray();
            menu.Style = Theme.Style("EffortPickerMenu");
            menu.MinWidth = 0;
            menu.Items.Insert(0, label);
            menu.Items.Insert(0, divider);
            menu.Items.Insert(0, EffortMenuItem(levels, () =>
            {
                bool show = !IsShown(divider);
                foreach (var item in hidden)
                {
                    item.Visibility = VisibleIf(show);
                    if (show) Settle(item);
                }
                return show;
            }));
            menu.Opened += (_, _) =>
            {
                if (!Theme.AnimationsEnabled || VisualTreeHelper.GetChild(menu, 0) is not FrameworkElement chrome) return;
                Settle(chrome);
                // A fading popup keeps its window for 150 ms after closing, long enough for the 50 ms fade;
                // Closed comes only after that, so the fade starts on IsOpen.
                ((Popup)menu.Parent).PopupAnimation = PopupAnimation.Fade;
                var isOpen = DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
                void FadeOut(object? sender, EventArgs e)
                {
                    if (menu.IsOpen) return;
                    isOpen.RemoveValueChanged(menu, FadeOut);
                    chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(50)));
                }
                isOpen.AddValueChanged(menu, FadeOut);
            };
        }
        else
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(FileActions.Item(_state.FastMode ? Tr("Выключить быстрый режим", "Turn off fast mode") : Tr("Включить быстрый режим", "Turn on fast mode"), Glyphs.Lightning,
                () => SetFastMode(!_state.FastMode)));
        }
        menu.IsOpen = true;
    }

    void OpenEconomyModelMenu()
    {
        var provider = _config.Provider;
        var menu = ModelMenu(ModelButton, provider, model =>
        {
            (_state.EconomyModels ??= [])[provider] = model;
            _state.Save();
        });
        var saved = _state.EconomyModels?.GetValueOrDefault(provider);
        foreach (var item in menu.Items.OfType<MenuItem>()) item.IsChecked = item.Tag is string model && model == saved;
        var off = FileActions.Item(Tr("Использовать выбранную модель для всех запросов", "Use the selected model for all requests"), null, () =>
        {
            _state.EconomyModels?.Remove(provider);
            _state.Save();
        });
        off.IsChecked = saved == null;
        menu.Items.Insert(0, off);
        menu.IsOpen = true;
    }

    static void Settle(UIElement element)
    {
        if (!Theme.AnimationsEnabled) return;
        static AnimationTimeline Ease(double from, double to) => new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(from, KeyTime.FromPercent(0)),
                new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)), new KeySpline(0, 0, 0.2, 1)),
            },
        };
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        element.BeginAnimation(OpacityProperty, Ease(0, 1));
        shift.BeginAnimation(TranslateTransform.YProperty, Ease(8, 0));
    }

    Task SwitchProviderAsync(AgentProvider provider) => RunChatOperationAsync(async () =>
    {
        if (AnyRunning)
        {
            ShowToast(Tr("Остановите агента, чтобы переключить провайдера", "Stop active tasks before switching providers"), ToastKind.Warning);
            return;
        }
        var previous = CurrentModelSelection();
        if (provider == previous.Provider || !SaveEnvChoice("AGENT_PROVIDER", provider.ToString().ToLowerInvariant())) return;
        CloseFlyout();
        ApplyProvider();
        UpdateContextBanner();
        await ShowModelChangeAsync(previous);
    });

    private bool SaveEnvChoice(string key, string value)
    {
        if (SaveEnv(new() { [key] = value }) is not { } error) return true;
        ShowToast(Tr("Не удалось сохранить выбор в .env: ", "Could not save the selection to .env: ") + error, ToastKind.Error);
        return false;
    }

    private ContextMenu ModelMenu(Button picker, AgentProvider provider, Action<string> choose)
    {
        var menu = new ContextMenu { PlacementTarget = picker, MaxHeight = 360, MinWidth = picker.ActualWidth };
        var names = _modelNames.GetValueOrDefault(provider);
        var current = picker == ModelButton ? CurrentModel() : picker.Content as string;
        foreach (var model in picker.Tag as IEnumerable<string> ?? [])
        {
            var item = FileActions.Item((names?.GetValueOrDefault(model) ?? model).Replace("_", "__"), null, () => choose(model));
            item.Tag = model;
            item.IsChecked = model == current;
            menu.Items.Add(item);
        }
        return menu;
    }

    private string[] EffortLevelsOf(string? model) => model == null ? [] : _models.TryGetValue(model, out var info)
        ? (info.Efforts ?? []).Where(EffortNames.ContainsKey).OrderBy(level => Array.IndexOf(AppConfig.EffortLevels, level)).Distinct().ToArray()
        : _config.Provider == AgentProvider.Groq ? Groq.EffortsOf(model) : [];

    private string? EffectiveEffort(string[] levels)
    {
        if (_config.ReasoningEffort is not { } saved || levels.Length == 0) return null;
        int rank = Array.IndexOf(AppConfig.EffortLevels, saved);
        return levels.LastOrDefault(level => Array.IndexOf(AppConfig.EffortLevels, level) <= rank) ?? levels[0];
    }

    private void UpdateModelEffort()
    {
        var model = CurrentModel();
        var levels = EffortLevelsOf(model);
        var effort = _agent.ReasoningEffort = EffectiveEffort(levels);
        bool loading = model == null && _loadingModelCatalog;
        ModelSkeleton.Visibility = VisibleIf(loading);
        ModelNameText.Visibility = VisibleIf(!loading);
        var label = ModelNameText.Text = model != null ? ModelDisplayName(model) : loading ? Tr("Загрузка моделей…", "Loading models…")
            : _provider != null ? Tr("Модели недоступны", "Models unavailable") : Tr("Выбрать модель", "Choose a model");
        bool showEffort = model != null && (levels.Length > 0 || _state.FastMode);
        ModelEffortText.Visibility = VisibleIf(showEffort);
        ModelEffortText.Text = (_state.FastMode ? Tr("Быстро · ", "Fast · ") : "") +
            (effort != null ? Tr(EffortNames[effort].Russian, EffortNames[effort].English) : Tr("Обычное", "Default"));
        AutomationProperties.SetName(ModelButton, showEffort ? Tr($"Модель {label}, рассуждение: {ModelEffortText.Text}", $"Model {label}, reasoning: {ModelEffortText.Text}")
            : model != null ? Tr($"Модель {label}", $"Model {label}") : label);
    }

    private MenuItem EffortMenuItem(string[] levels, Func<bool> toggleModels)
    {
        string?[] values = [null, .. levels];
        int committed = Array.IndexOf(values, EffectiveEffort(levels));
        var modelName = ModelNameText.Text;
        var heading = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) };
        heading.SetResourceReference(ForegroundProperty, "AccentText");
        var chevron = new TextBlock
        {
            Text = Glyphs.ChevronDown, FontFamily = Theme.Icons, FontSize = 10, Margin = new Thickness(4, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        }.Paint(ForegroundProperty, "TextDim");
        var caption = new TextBlock { Text = modelName, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 }.Paint(ForegroundProperty, "TextDim");
        var choose = new Button
        {
            Style = Theme.Style("RowButton"),
            Content = new StackPanel
            {
                Children = { heading, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { caption, chevron } } },
            },
            MinWidth = 102, Height = 40, Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(choose, Tr("Выбрать модель · ", "Choose a model · ") + modelName);
        choose.Click += (_, _) => chevron.Text = toggleModels() ? Glyphs.ChevronDown : Glyphs.ChevronRight;
        var slider = new EffortSlider { Style = Theme.Style("EffortSlider"), Maximum = values.Length - 1, Value = committed, Margin = new Thickness(0, 6, 0, 0) };
        var reset = Theme.IconButton(Glyphs.Refresh, Tr("Сбросить силу рассуждения", "Reset reasoning effort"), 28);
        reset.HorizontalAlignment = HorizontalAlignment.Right;
        reset.VerticalAlignment = VerticalAlignment.Top;
        reset.RenderTransform = new ScaleTransform(-1, 1);
        reset.RenderTransformOrigin = new Point(0.5, 0.5);
        reset.Click += (_, _) => slider.Value = 0;
        bool dragging = false, restoring = false;
        void ShowValue()
        {
            var value = values[(int)slider.Value];
            var entry = value != null ? EffortNames[value] : ("Обычное", "Default", "как решит модель", "Model decides");
            var (russian, english, russianHint, englishHint) = entry;
            var name = Tr(russian, english);
            var hint = Tr(russianHint, englishHint);
            heading.Text = name;
            slider.ToolTip = name + " · " + hint;
            AutomationProperties.SetName(slider, Tr("Сила рассуждения · ", "Reasoning effort · ") + name);
            reset.IsEnabled = value != null;
        }
        void RestoreValue()
        {
            restoring = true;
            slider.Value = committed;
            restoring = false;
        }
        void Commit()
        {
            int selected = (int)slider.Value;
            if (selected == committed) return;
            if (SaveEnvChoice(AppConfig.KeyOf(_config.Provider, "REASONING_EFFORT"), values[selected] ?? ""))
            {
                committed = selected;
                UpdateModelEffort();
            }
            else RestoreValue();
        }
        slider.ValueChanged += (_, _) =>
        {
            ShowValue();
            if (!dragging && !restoring) Commit();
        };
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            dragging = false;
            if (e.Canceled) RestoreValue();
            else Commit();
        }));
        ShowValue();
        var lightning = new ToggleButton
        {
            Style = Theme.Style("IconToggle"), Content = Glyphs.Lightning, Width = 28, Height = 28, IsChecked = _state.FastMode,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            ToolTip = Tr("Параллельный поиск и больше независимых проверок с субагентами", "Parallel searches and more independent checks with subagents"),
        };
        AutomationProperties.SetName(lightning, Tr("Быстрый режим", "Fast mode"));
        lightning.Checked += (_, _) => SetFastMode(true);
        lightning.Unchecked += (_, _) => SetFastMode(false);
        return new MenuItem
        {
            Header = new StackPanel
            {
                Margin = new Thickness(12, 8, 12, 12),
                Children = { new Grid { Margin = new Thickness(-6, 0, -6, 0), Children = { lightning, choose, reset } }, slider },
            },
            Style = Theme.Style("EffortHeader"),
        };
    }

    void SetFastMode(bool on)
    {
        _state.FastMode = on;
        _state.Save();
        ApplyFastMode();
        ShowToast(on ? Tr("Быстрый режим включён", "Fast mode on") : Tr("Быстрый режим выключен", "Fast mode off"));
    }

    private void ApplyFastMode()
    {
        _agent.FastMode = _state.FastMode;
        _agent.UseSubagents = _config.UseSubagents;
        UpdateModelEffort();
    }

    private void EnsureChatModel()
    {
        var provider = _config.Provider;
        if (_selectedModel is not { } model || _modelLists.GetValueOrDefault(provider) is not { Count: > 0 } known ||
            known.Contains(model, StringComparer.OrdinalIgnoreCase)) return;
        _selectedModel = null;
        var values = new Dictionary<string, string?> { [AppConfig.KeyOf(provider, "MODEL")] = null };
        if (provider == AgentProvider.Tooken && _config.ImageModel == null && _models.GetValueOrDefault(model)?.Images == true)
            values["TOOKEN_IMAGE_MODEL"] = model;
        SaveEnv(values);
        ShowToast(Tr($"Модель {model} недоступна для чата. Выбрана {known[0]}.", $"{model} is unavailable for chat. Selected {known[0]}."));
    }
}
