using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Ui;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    private readonly Dictionary<AgentProvider, Dictionary<string, string>> _modelNames = new();
    private string? _selectedModel;
    private bool _loadingModelCatalog;

    private static string ProviderName(AgentProvider provider) => provider switch
    {
        AgentProvider.ChatGpt => "ChatGPT",
        AgentProvider.Groq => "Groq",
        _ => "Tooken Club",
    };

    private static string[] DefaultModelsOf(AgentProvider provider) => provider switch
    {
        AgentProvider.ChatGpt => ChatGptModels,
        AgentProvider.Groq => GroqModels,
        _ => DefaultModels,
    };

    private bool IsReady(AgentProvider provider) => provider switch
    {
        AgentProvider.ChatGpt => _chatGpt.PlanAllowed,
        AgentProvider.Groq => _config.HasGroqKey,
        _ => _config.HasApiKey,
    };

    private string NotReadyText() => _config.Provider switch
    {
        AgentProvider.ChatGpt when _chatGpt.Connected => "Разрешите Sharp Agent использовать подписку ChatGPT в настройках",
        AgentProvider.ChatGpt => "Сначала войдите в ChatGPT в настройках",
        AgentProvider.Groq => "Сначала укажите API-ключ Groq в настройках",
        _ => "Сначала укажите API-ключ tooken.club",
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
            AgentProvider.Groq when _config.HasGroqKey => new ChatApiClient(Groq.BaseUrl, _config.GroqApiKey!, AgentProvider.Groq),
            AgentProvider.Tooken => _tooken,
            _ => null,
        };
        if (_provider is ChatGptClient chat) chat.LimitsChanged += OnLimitsChanged;
        else if (provider == AgentProvider.Groq && _provider is ChatApiClient groq) groq.LimitsChanged += OnLimitsChanged;
        _agent.Api = _provider;
        _agent.Provider = provider;
        var images = _tooken;
        _toolContext.GenerateImage = images == null
            ? null
            : (prompt, size, ct) => images.GenerateImageAsync(_config.ImageModel ?? AppConfig.DefaultImageModel, prompt, size, ct);

        _models = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
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
                              (Enum.GetValues<AgentProvider>().Count(IsReady) > 1 ? " или провайдера" : "") +
                              (_provider is ChatGptClient ? ".\nЗапросы расходуют вашу подписку ChatGPT" : "");
        UpdateModelEffort();

        HideBanner("plan");
        if (_provider != null) HideBanner("key");
        else if (provider == AgentProvider.ChatGpt && _chatGpt.Connected)
            ShowBanner("key", Glyphs.Warning, "Подписка ChatGPT не подключена",
                "Вы вошли, но не разрешили Sharp Agent использовать подписку. Разрешите это в настройках " +
                "или выберите там другого провайдера — Tooken Club или Groq.",
                "Разрешить", ShowSettings);
        else if (provider == AgentProvider.ChatGpt)
            ShowBanner("key", Glyphs.Warning, "Нужен вход в ChatGPT",
                "Войдите на сайте OpenAI и разрешите Sharp Agent использовать подписку.",
                "Войти", ShowSettings);
        else
            ShowBanner("key", Glyphs.Warning, provider == AgentProvider.Groq ? "Нужен API-ключ Groq" : "Нужен API-ключ tooken.club",
                "Введите API-ключ, чтобы агент мог отвечать.",
                "Указать ключ", ShowSettings);

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
            ShowBanner("plan", Glyphs.Warning, "Ограничение подписки ChatGPT", limit, "Управлять использованием",
                () => FileActions.OpenUrl(ChatGptClient.UsageUrl), warning: true);
        else HideBanner("plan");
    }

    private void ShowPlanWelcome()
    {
        var text = new TextBlock { FontSize = 14, Foreground = Theme.TextSoft, TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Run("Запросы в Sharp Agent будут расходовать вашу подписку ChatGPT. Сколько из неё уходит на Sharp Agent " +
                                 "и какие у него лимиты, смотрите и меняйте в настройках ChatGPT. "));
        var link = new Hyperlink(new Run("Управлять использованием")) { ToolTip = "Открыть настройки использования ChatGPT" };
        link.Click += (_, _) => FileActions.OpenUrl(ChatGptClient.UsageUrl);
        text.Inlines.Add(link);
        DarkDialog.Show(this, "Вы используете подписку ChatGPT", text, closeText: "Понятно");
    }

    private async Task LoadModelListAsync(IChatProvider api)
    {
        var provider = _config.Provider;
        var key = ModelCatalogKey(provider);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var models = await api.ListModelsAsync(cts.Token);
            if (models.Count == 0 || !ReferenceEquals(api, _provider)) return;
            ApplyModelCatalog(provider, models);
            if (key != null)
            {
                (_state.ModelCatalogs ??= new())[provider] = new ModelCatalogCache(key, models);
                _state.Save();
            }
            if (_cts == null) EnsureChatModel();
            UpdateContextBanner();
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (!ReferenceEquals(api, _provider)) return;
            if (provider == AgentProvider.ChatGpt && !IsReady(provider) && _cts == null)
            {
                ApplyProvider();
                return;
            }
            ShowBanner("key", Glyphs.Warning, provider switch
                {
                    AgentProvider.ChatGpt => "ChatGPT не принял вход",
                    AgentProvider.Groq => "Groq не принял API-ключ",
                    _ => "Сервер не принял API-ключ",
                }, ex.Message.Split('\n')[0], "Открыть настройки", ShowSettings, warning: true);
        }
        catch
        {
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

    private string? ModelCatalogKey(AgentProvider provider) => provider switch
    {
        AgentProvider.Tooken when _config.HasApiKey => UsageTracker.KeyId(_config.ApiKey!, _config.BaseUrl),
        AgentProvider.Groq when _config.HasGroqKey => UsageTracker.KeyId(_config.GroqApiKey!, Groq.BaseUrl),
        AgentProvider.ChatGpt when _chatGpt.PlanAllowed && _chatGpt.ClientId is { } client =>
            UsageTracker.KeyId(client, "https://api.openai.com/v1/models"),
        _ => null,
    };

    private void ApplyModelCatalog(AgentProvider provider, List<ModelInfo> models)
    {
        RememberModelList(provider, models);
        _models = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models) _models[model.Id] = model;
        ModelButton.Tag = _modelLists.GetValueOrDefault(provider) ?? (IEnumerable<string>)DefaultModelsOf(provider);
    }

    private void RememberModelList(AgentProvider provider, List<ModelInfo> models)
    {
        if (models.Count == 0) return;
        _modelLists[provider] = models.Where(m => m.Chat).Select(m => m.Id).ToList();
        var names = _modelNames[provider] = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in models)
            if (model.Name is { Length: > 0 } name) names[model.Id] = name;
        if (provider == AgentProvider.Tooken) _imageModels = models.Where(m => m.Images).Select(m => m.Id).ToList();
    }

    private string CurrentModel() => _selectedModel ?? _config.Model;

    private string ModelDisplayName(string model) =>
        _modelNames.GetValueOrDefault(_config.Provider)?.GetValueOrDefault(model) ?? model;

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
        if (ready.Count > 1 || (ready.Count == 1 && ready[0] != _config.Provider))
        {
            menu.Items.Insert(0, new Separator());
            foreach (var provider in Enumerable.Reverse(ready))
            {
                var item = FileActions.Item(ProviderName(provider), provider == _config.Provider ? Glyphs.Check : null,
                    () => SwitchProvider(provider));
                item.FontWeight = FontWeights.SemiBold;
                AutomationProperties.SetName(item, "Провайдер " + ProviderName(provider));
                menu.Items.Insert(0, item);
            }
        }
        if (_provider is ChatGptClient)
        {
            menu.Items.Add(new Separator());
            var note = FileActions.Item("Используется подписка ChatGPT", Glyphs.Info, () => { });
            note.IsEnabled = false;
            menu.Items.Add(note);
            menu.Items.Add(FileActions.Item("Управлять использованием", Glyphs.Open, () => FileActions.OpenUrl(ChatGptClient.UsageUrl)));
        }
        if (EffortLevelsOf(CurrentModel()) is { Length: > 0 } levels)
        {
            var modelItems = menu.Items.OfType<UIElement>().ToArray();
            foreach (var item in modelItems) item.Visibility = Visibility.Collapsed;
            var divider = new Separator { Visibility = Visibility.Collapsed };
            menu.Style = Theme.Style("EffortPickerMenu");
            menu.MinWidth = 0;
            menu.Items.Insert(0, divider);
            menu.Items.Insert(0, EffortMenuItem(levels, () =>
            {
                var visibility = divider.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                divider.Visibility = visibility;
                foreach (var item in modelItems) item.Visibility = visibility;
                return visibility == Visibility.Visible;
            }));
        }
        menu.IsOpen = true;
    }

    private void SwitchProvider(AgentProvider provider)
    {
        if (provider == _config.Provider) return;
        if (SaveEnv(new Dictionary<string, string?> { ["AGENT_PROVIDER"] = provider.ToString().ToLowerInvariant() }) is { } error)
        {
            ShowToast("Не удалось сохранить выбор в .env: " + error, ToastKind.Error);
            return;
        }
        CloseFlyout();
        ApplyProvider();
        UpdateContextBanner();
    }

    private ContextMenu ModelMenu(Button picker, AgentProvider provider, Action<string> choose)
    {
        var menu = new ContextMenu { PlacementTarget = picker, MaxHeight = 360, MinWidth = picker.ActualWidth };
        var names = _modelNames.GetValueOrDefault(provider);
        var current = picker == ModelButton ? CurrentModel() : picker.Content as string;
        foreach (var model in picker.Tag as IEnumerable<string> ?? [])
            menu.Items.Add(FileActions.Item((names?.GetValueOrDefault(model) ?? model).Replace("_", "__"),
                model == current ? Glyphs.Check : null, () => choose(model)));
        return menu;
    }

    private void PickModel(Button picker, AgentProvider provider, Action<string> choose) =>
        ModelMenu(picker, provider, choose).IsOpen = true;

    private string[] EffortLevelsOf(string model) =>
        _models.TryGetValue(model, out var info)
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
        var levels = EffortLevelsOf(CurrentModel());
        var effort = EffectiveEffort(levels);
        _agent.ReasoningEffort = effort;
        var label = ModelDisplayName(CurrentModel());
        ModelNameText.Text = label;
        var pending = _loadingModelCatalog && !_models.ContainsKey(CurrentModel()) ? _config.ReasoningEffort : null;
        bool showEffort = levels.Length > 0 || pending != null;
        ModelEffortText.Visibility = showEffort ? Visibility.Visible : Visibility.Collapsed;
        ModelEffortText.Text = effort != null ? EffortNames[effort].Label :
            pending != null ? EffortNames[pending].Label + "…" : "Обычное";
        AutomationProperties.SetName(ModelButton, !showEffort ? $"Модель {label}" :
            $"Модель {label}, рассуждение: {ModelEffortText.Text}");
    }

    private MenuItem EffortMenuItem(string[] levels, Func<bool> toggleModels)
    {
        var values = new string?[] { null }.Concat(levels).ToArray();
        int committed = Array.IndexOf(values, EffectiveEffort(levels));
        var model = CurrentModel();
        var caption = new TextBlock
        {
            Text = ModelDisplayName(model),
            FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.TextSoft, MaxWidth = 135,
        };
        var heading = new TextBlock
        {
            FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
        };
        heading.SetResourceReference(ForegroundProperty, "AccentText");
        var chevron = new TextBlock
        {
            Text = Glyphs.ChevronRight, FontFamily = Theme.Icons, FontSize = 8, Foreground = Theme.TextMuted,
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var modelLine = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        modelLine.Children.Add(caption);
        modelLine.Children.Add(chevron);
        var label = new StackPanel();
        label.Children.Add(heading);
        label.Children.Add(modelLine);
        var choose = new Button
        {
            Style = Theme.Style("RowButton"), Content = label, MinWidth = 102, Height = 40,
            Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = "Выбрать модель",
        };
        AutomationProperties.SetName(choose, "Выбрать модель · " + caption.Text);
        choose.Click += (_, _) => chevron.Text = toggleModels() ? Glyphs.ChevronDown : Glyphs.ChevronRight;

        var slider = new EffortSlider
        {
            Style = Theme.Style("EffortSlider"), Maximum = values.Length - 1, Value = committed,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var reset = Theme.IconButton(Glyphs.Refresh, "Сбросить силу рассуждения", 24, 14);
        reset.HorizontalAlignment = HorizontalAlignment.Right;
        reset.VerticalAlignment = VerticalAlignment.Top;
        reset.RenderTransform = new ScaleTransform(-1, 1);
        reset.RenderTransformOrigin = new Point(0.5, 0.5);
        reset.Click += (_, _) => slider.Value = 0;
        var top = new Grid { Margin = new Thickness(-6, 0, -4, 0) };
        top.Children.Add(new TextBlock
        {
            Text = Glyphs.Lightning, FontFamily = Theme.Icons, FontSize = 13, Foreground = Theme.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 6, 0, 0),
        });
        top.Children.Add(choose);
        top.Children.Add(reset);
        var card = new StackPanel { Margin = new Thickness(12, 6, 12, 10) };
        card.Children.Add(top);
        card.Children.Add(slider);

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
            var key = AppConfig.KeyOf(_config.Provider, "REASONING_EFFORT");
            if (SaveEnv(new Dictionary<string, string?> { [key] = values[selected] ?? "" }) is { } error)
            {
                RestoreValue();
                ShowToast("Не удалось сохранить выбор в .env: " + error, ToastKind.Error);
                return;
            }
            committed = selected;
            UpdateModelEffort();
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
        return new MenuItem { Header = card, Style = Theme.Style("EffortHeader") };
    }

    private bool DrawsOnly(string model) => _models.TryGetValue(model, out var info) && !info.Chat;

    private void EnsureChatModel()
    {
        var model = CurrentModel();
        var provider = _config.Provider;
        if (provider != AgentProvider.Tooken)
        {
            var known = _modelLists.GetValueOrDefault(provider) ?? [];
            if (known.Count == 0 || known.Contains(model)) return;
            var fallback = known.Contains(AppConfig.DefaultModelOf(provider)) ? AppConfig.DefaultModelOf(provider) : known[0];
            _selectedModel = fallback;
            RememberModel(fallback);
            ShowToast($"Модели {model} нет у {ProviderName(provider)}, выбрана {fallback}.");
            return;
        }

        if (!DrawsOnly(model)) return;
        var chat = DrawsOnly(AppConfig.DefaultModel) || !_models.ContainsKey(AppConfig.DefaultModel)
            ? _models.Values.Where(m => m.Chat).Select(m => m.Id).FirstOrDefault()
            : AppConfig.DefaultModel;
        if (chat == null) return;

        _selectedModel = chat;
        var values = new Dictionary<string, string?> { ["TOOKEN_MODEL"] = chat };
        if (_config.ImageModel == null && _models[model].Images) values["TOOKEN_IMAGE_MODEL"] = model;
        SaveEnv(values);
        ShowToast($"{model} рисует картинки, но не ведёт чат, поэтому для разговора выбрана {chat}. " +
                  "Попросите нарисовать — агент сделает это сам.");
    }

    private void RememberModel(string model)
    {
        if (!string.Equals(model, _config.Model, StringComparison.Ordinal))
            SaveEnv(new Dictionary<string, string?> { [AppConfig.KeyOf(_config.Provider, "MODEL")] = model });
    }
}
