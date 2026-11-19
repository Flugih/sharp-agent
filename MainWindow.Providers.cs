using System.Windows.Documents;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    private readonly Dictionary<AgentProvider, Dictionary<string, string>> _modelNames = new();
    private string? _selectedModel;
    private bool _loadingModelCatalog;

    private static string ProviderName(AgentProvider provider) => provider switch
    {
        AgentProvider.ChatGpt => "ChatGPT", AgentProvider.Groq => "Groq", AgentProvider.Gemini => "Google Gemini",
        AgentProvider.OpenRouter => "OpenRouter", AgentProvider.Mistral => "Mistral AI", _ => "Tooken Club",
    };

    private static string[] DefaultModelsOf(AgentProvider provider) =>
        provider switch
        {
            AgentProvider.ChatGpt => ChatGptModels, AgentProvider.Groq => GroqModels, AgentProvider.Tooken => DefaultModels,
            _ => [AppConfig.DefaultModelOf(provider)],
        };

    private bool IsReady(AgentProvider provider) =>
        provider == AgentProvider.ChatGpt ? _chatGpt.PlanAllowed : _config.ApiKeyFor(provider) != null;

    private string NotReadyText() => _config.Provider switch
    {
        AgentProvider.ChatGpt when _chatGpt.Connected => "Разрешите Sharp Agent использовать подписку ChatGPT в настройках",
        AgentProvider.ChatGpt => "Сначала войдите в ChatGPT в настройках",
        _ => "Сначала укажите API-ключ в настройках",
    };

    private void ApplyProvider()
    {
        var provider = _config.Provider;
        if (!ReferenceEquals(_provider, _tooken)) _provider?.Dispose();
        _tooken?.Dispose();
        _tooken = _config.HasApiKey ? new ChatApiClient(_config.BaseUrl, _config.ApiKey!) : null;
        _provider = provider switch
        {
            AgentProvider.ChatGpt when _chatGpt.PlanAllowed => new ChatGptClient(_chatGpt),
            AgentProvider.Tooken => _tooken,
            _ when _config.ApiKeyFor(provider) is { } key => new ChatApiClient(AppConfig.DefaultBaseUrlOf(provider), key, provider),
            _ => null,
        };
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
        if (ModelCatalogKey(provider) is { } catalogKey &&
            _state.ModelCatalogs?.GetValueOrDefault(provider) is { Models.Count: > 0 } cached && cached.Connection == catalogKey)
            ApplyModelCatalog(provider, cached.Models);
        else
        {
            if (provider == AgentProvider.Tooken) _imageModels = null;
            ModelButton.Tag = DefaultModelsOf(provider);
        }
        _loadingModelCatalog = _provider != null;
        _selectedModel = _config.Model;
        ModelButton.ToolTip = $"Модель {ProviderName(provider)}, которая отвечает в чате. Щелчок — выбрать модель" +
            (Enum.GetValues<AgentProvider>().Count(IsReady) > 1 ? " или провайдера" : "") + (_provider is ChatGptClient ? ".\nЗапросы расходуют вашу подписку ChatGPT" : "");
        UpdateModelEffort();
        HideBanner("plan");
        if (_provider != null) HideBanner("key");
        else
        {
            var (title, text, action) = provider != AgentProvider.ChatGpt
                ? ("Нужен API-ключ", "Введите API-ключ, чтобы агент мог отвечать.", "Указать ключ")
                : _chatGpt.Connected ? ("Подписка ChatGPT не подключена",
                    "Разрешите Sharp Agent использовать подписку в настройках или выберите другого провайдера.", "Разрешить")
                : ("Нужен вход в ChatGPT", "Войдите на сайте OpenAI и разрешите Sharp Agent использовать подписку.", "Войти");
            ShowBanner("key", Glyphs.Warning, title, text, action, ShowSettings);
        }
        _usage.Reset();
        _usageUnavailable = false;
        _usageError = null;
        UpdateBalanceIndicator();
        if (_provider is not ChatGptClient) _ = _chatGpt.RenewAsync();
        if (_provider == null) return;
        _ = LoadModelListAsync(_provider);
        _ = RefreshUsageAsync();
    }

    private void OnLimitsChanged()
    {
        UpdateBalanceIndicator();
        if (FlyoutShows(UsageCard)) BuildUsagePanel();
        if ((_provider as ChatGptClient)?.LimitMessage is { } limit)
            ShowBanner("plan", Glyphs.Warning, "Ограничение подписки ChatGPT", limit, "Управлять использованием", () => FileActions.OpenUrl(ChatGptClient.UsageUrl), warning: true);
        else HideBanner("plan");
    }

    private void ShowPlanWelcome() => DarkDialog.Show(this, "Вы используете подписку ChatGPT", new TextBlock
    {
        FontSize = 14, Foreground = Theme.TextSoft, TextWrapping = TextWrapping.Wrap,
        Inlines =
        {
            new Run("Запросы в Sharp Agent будут расходовать вашу подписку ChatGPT. Сколько из неё уходит на Sharp Agent и какие у него лимиты, смотрите и меняйте в настройках ChatGPT. "),
            MakeLink("Управлять использованием", () => FileActions.OpenUrl(ChatGptClient.UsageUrl), "Открыть настройки использования ChatGPT"),
        },
    }, closeText: "Понятно");

    private async Task LoadModelListAsync(IChatProvider api)
    {
        var provider = _config.Provider;
        var connection = ModelCatalogKey(provider);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await api.ListModelsAsync(cts.Token);
            if (models.Count == 0 || !ReferenceEquals(api, _provider)) return;
            ApplyModelCatalog(provider, models);
            CacheModelCatalog(provider, connection, models);
            if (SettingsOpen && _settingsProvider == provider && SettingsCatalogKey() == connection)
                DefaultModelButton.Tag = ChatModelChoices(provider);
            if (_cts == null) EnsureChatModel();
            UpdateContextBanner();
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (!ReferenceEquals(api, _provider)) return;
            if (provider == AgentProvider.ChatGpt && !IsReady(provider) && _cts == null) ApplyProvider();
            else
                ShowBanner("key", Glyphs.Warning, provider switch
                {
                    AgentProvider.ChatGpt => "ChatGPT не принял вход",
                    AgentProvider.Groq => "Groq не принял API-ключ",
                    _ => "Сервер не принял API-ключ",
                }, ex.Message.Split('\n')[0], "Открыть настройки", ShowKeySettings, warning: true);
        }
        catch { }
        finally
        {
            if (ReferenceEquals(api, _provider))
            {
                _loadingModelCatalog = false;
                UpdateModelEffort();
            }
        }
    }

    private string? ModelCatalogKey(AgentProvider provider) =>
        ModelCatalogKey(provider, _config.ApiKeyFor(provider), _config.BaseUrl);

    private string? ModelCatalogKey(AgentProvider provider, string? key, string tookenBaseUrl) => provider switch
    {
        AgentProvider.ChatGpt when _chatGpt.PlanAllowed && _chatGpt.ClientId is { } client => UsageTracker.KeyId(client, "https://api.openai.com/v1/models"),
        AgentProvider.ChatGpt => null,
        _ when !string.IsNullOrEmpty(key) => UsageTracker.KeyId(key, provider == AgentProvider.Tooken ? tookenBaseUrl : AppConfig.DefaultBaseUrlOf(provider)),
        _ => null,
    };

    private void CacheModelCatalog(AgentProvider provider, string? connection, List<ModelInfo> models)
    {
        if (connection == null || models.Count == 0) return;
        (_state.ModelCatalogs ??= new())[provider] = new(connection, models);
        _state.Save();
    }

    private IEnumerable<string> ChatModelChoices(AgentProvider provider) =>
        _modelLists.GetValueOrDefault(provider) is { Count: > 0 } models ? models : DefaultModelsOf(provider);

    private void ApplyModelCatalog(AgentProvider provider, List<ModelInfo> models)
    {
        RememberModelList(provider, models);
        _models = new(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models) _models[model.Id] = model;
        ModelButton.Tag = ChatModelChoices(provider);
    }

    private void RememberModelList(AgentProvider provider, List<ModelInfo> models)
    {
        if (models.Count == 0) return;
        _modelLists[provider] = models.Where(m => m.Chat && string.Equals(m.Id, AppConfig.ChatModelOf(provider, m.Id), StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = _modelNames[provider] = new(StringComparer.Ordinal);
        foreach (var model in models)
            if (model.Name is { Length: > 0 } name) names[model.Id] = name;
        if (provider == AgentProvider.Tooken) _imageModels = models.Where(m => m.Images).Select(m => m.Id).ToList();
    }

    private string CurrentModel() => _selectedModel ?? _config.Model;

    private string ModelDisplayName(string model) => _modelNames.GetValueOrDefault(_config.Provider)?.GetValueOrDefault(model) ?? model;

    private void ModelButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = ModelMenu(ModelButton, _config.Provider, model =>
        {
            _selectedModel = model;
            RememberModel(model);
            UpdateModelEffort();
            UpdateContextBanner();
        });
        var ready = Enum.GetValues<AgentProvider>().Where(IsReady).ToList();
        if (ready.Exists(p => p != _config.Provider))
        {
            menu.Items.Insert(0, new Separator());
            foreach (var provider in Enumerable.Reverse(ready))
            {
                var item = FileActions.Item(ProviderName(provider), null, () => SwitchProvider(provider));
                item.IsChecked = provider == _config.Provider;
                item.FontWeight = FontWeights.SemiBold;
                AutomationProperties.SetName(item, "Провайдер " + ProviderName(provider));
                menu.Items.Insert(0, item);
            }
        }
        if (_provider is ChatGptClient)
        {
            var note = FileActions.Item("Используется подписка ChatGPT", Glyphs.Info, () => { });
            note.IsEnabled = false;
            menu.Items.Add(new Separator());
            menu.Items.Add(note);
            menu.Items.Add(FileActions.Item("Управлять использованием", Glyphs.Open, () => FileActions.OpenUrl(ChatGptClient.UsageUrl)));
        }
        if (EffortLevelsOf(CurrentModel()) is { Length: > 0 } levels)
        {
            var divider = new Separator();
            var label = new MenuItem { Header = new TextBlock { Style = Theme.Style("MenuLabel"), Text = "Выбрать модель" }, Style = Theme.Style("EffortHeader"), Margin = new(6, 0, 6, 0) };
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
            menu.Items.Add(FileActions.Item(_state.FastMode ? "Выключить быстрый режим" : "Включить быстрый режим", Glyphs.Lightning,
                () => SetFastMode(!_state.FastMode)));
        }
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

    private void SwitchProvider(AgentProvider provider)
    {
        if (_cts != null)
        {
            ShowToast("Остановите агента, чтобы переключить провайдера", ToastKind.Warning);
            return;
        }
        if (provider == _config.Provider || !SaveEnvChoice("AGENT_PROVIDER", provider.ToString().ToLowerInvariant())) return;
        CloseFlyout();
        ApplyProvider();
        UpdateContextBanner();
    }

    private bool SaveEnvChoice(string key, string value)
    {
        if (SaveEnv(new() { [key] = value }) is not { } error) return true;
        ShowToast("Не удалось сохранить выбор в .env: " + error, ToastKind.Error);
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
            item.IsChecked = model == current;
            menu.Items.Add(item);
        }
        return menu;
    }

    private void PickModel(Button picker, AgentProvider provider) => ModelMenu(picker, provider, model => picker.Content = model).IsOpen = true;

    private string[] EffortLevelsOf(string model) => _models.TryGetValue(model, out var info)
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
        var label = ModelNameText.Text = ModelDisplayName(model);
        var pending = _loadingModelCatalog && !_models.ContainsKey(model) ? _config.ReasoningEffort : null;
        bool showEffort = levels.Length > 0 || pending != null || _state.FastMode;
        ModelEffortText.Visibility = VisibleIf(showEffort);
        ModelEffortText.Text = (_state.FastMode ? "Быстро · " : "") +
            (effort != null ? EffortNames[effort].Label : pending != null ? EffortNames[pending].Label + "…" : "Обычное");
        AutomationProperties.SetName(ModelButton, showEffort ? $"Модель {label}, рассуждение: {ModelEffortText.Text}" : $"Модель {label}");
    }

    private MenuItem EffortMenuItem(string[] levels, Func<bool> toggleModels)
    {
        string?[] values = [null, .. levels];
        int committed = Array.IndexOf(values, EffectiveEffort(levels));
        var modelName = ModelDisplayName(CurrentModel());
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
            HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = "Выбрать модель",
        };
        AutomationProperties.SetName(choose, "Выбрать модель · " + modelName);
        choose.Click += (_, _) => chevron.Text = toggleModels() ? Glyphs.ChevronDown : Glyphs.ChevronRight;
        var slider = new EffortSlider { Style = Theme.Style("EffortSlider"), Maximum = values.Length - 1, Value = committed, Margin = new Thickness(0, 6, 0, 0) };
        var reset = Theme.IconButton(Glyphs.Refresh, "Сбросить силу рассуждения", 28);
        reset.HorizontalAlignment = HorizontalAlignment.Right;
        reset.VerticalAlignment = VerticalAlignment.Top;
        reset.RenderTransform = new ScaleTransform(-1, 1);
        reset.RenderTransformOrigin = new Point(0.5, 0.5);
        reset.Click += (_, _) => slider.Value = 0;
        bool dragging = false, restoring = false;
        void ShowValue()
        {
            var value = values[(int)slider.Value];
            var (name, hint) = value != null ? EffortNames[value] : ("Обычное", "как решит модель");
            heading.Text = name;
            slider.ToolTip = name + " · " + hint;
            AutomationProperties.SetName(slider, "Сила рассуждения · " + name);
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
            ToolTip = "Параллельный поиск и чтение кода с обычной глубиной проверки",
        };
        AutomationProperties.SetName(lightning, "Быстрый режим");
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
        ShowToast(on ? "Быстрый режим включён" : "Быстрый режим выключен");
    }

    private void ApplyFastMode()
    {
        _agent.FastMode = _state.FastMode;
        _agent.UseSubagents = _config.UseSubagents;
        UpdateModelEffort();
    }

    private bool DrawsOnly(string model) => _models.TryGetValue(model, out var info) && !info.Chat;

    private void EnsureChatModel()
    {
        var model = CurrentModel();
        var provider = _config.Provider;
        if (provider != AgentProvider.Tooken)
        {
            if (_modelLists.GetValueOrDefault(provider) is not { Count: > 0 } known || known.Contains(model, StringComparer.OrdinalIgnoreCase)) return;
            var fallback = AppConfig.DefaultModelOf(provider);
            if (!known.Contains(fallback)) fallback = known[0];
            _selectedModel = fallback;
            RememberModel(fallback);
            ShowToast($"Модели {model} нет у {ProviderName(provider)}, выбрана {fallback}.");
            return;
        }
        if (!DrawsOnly(model)) return;
        var chat = _models.GetValueOrDefault(AppConfig.DefaultModel)?.Chat == true ? AppConfig.DefaultModel : _models.Values.FirstOrDefault(m => m.Chat)?.Id;
        if (chat == null) return;
        _selectedModel = chat;
        var values = new Dictionary<string, string?> { ["TOOKEN_MODEL"] = chat };
        if (_config.ImageModel == null && _models[model].Images) values["TOOKEN_IMAGE_MODEL"] = model;
        SaveEnv(values);
        ShowToast($"{model} рисует картинки, но не ведёт чат, поэтому для разговора выбрана {chat}. Попросите нарисовать — агент сделает это сам.");
    }

    private void RememberModel(string model)
    {
        if (model != _config.Model) SaveEnv(new() { [AppConfig.KeyOf(_config.Provider, "MODEL")] = model });
    }
}
