using System.Windows.Documents;
using System.Windows.Shell;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    private const double ContextWarning = 0.85;

    private bool _usageUnavailable;
    private object? _usageLoading;
    private string? _usageError;
    private Task<bool>? _limitAsk;

    private bool UsageLoading => _usageLoading != null && ReferenceEquals(_usageLoading, _provider);

    private async Task RefreshUsageAsync()
    {
        if (_tooken is not { } tooken || !ReferenceEquals(_provider, tooken) || _usageUnavailable || ReferenceEquals(_usageLoading, tooken)) return;
        _usageLoading = tooken;
        string? error = null;
        bool unavailable = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await _usage.RefreshAsync(tooken, UsageTracker.KeyId(_config.ApiKey!, _config.BaseUrl), cts.Token);
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented) { unavailable = true; }
        catch (Exception ex) { error = ex is OperationCanceledException ? Tr("сервер не ответил вовремя", "server did not respond in time") : ex.Message.Split('\n')[0]; }
        if (!ReferenceEquals(tooken, _provider)) return;
        _usageLoading = null;
        _usageError = error;
        _usageUnavailable |= unavailable;
        UpdateBalanceIndicator();
        if (FlyoutShows(UsageCard)) BuildUsagePanel();
    }

    private void UpdateBalanceIndicator()
    {
        var provider = _config.Provider;
        var snapshot = _usage.Last;
        bool tooken = provider == AgentProvider.Tooken, warning = provider switch
        {
            AgentProvider.ChatGpt => (_provider as ChatGptClient)?.LimitMessage != null,
            AgentProvider.Groq => (_provider as ChatApiClient)?.Limits is { } limits && (Low(limits.RequestsLeft, limits.Requests) || Low(limits.TokensLeft, limits.Tokens)),
            _ => snapshot != null && LimitReached(snapshot) != null,
        };
        var text = provider switch
        {
            AgentProvider.Tooken => snapshot != null ? Tr("Баланс ", "Balance ") + Tokens.Format(snapshot.Balance) : Tr("Баланс и расход", "Balance and usage"),
            AgentProvider.ChatGpt or AgentProvider.Groq => Tr("Лимиты ", "Limits for ") + ProviderName(provider),
            _ => Tr("Расход ", "Usage for ") + ProviderName(provider),
        };
        BalanceButton.Visibility = VisibleIf(!(tooken && _usageUnavailable));
        BalanceText.Text = text;
        if (warning) BalanceButton.Paint(ForegroundProperty, "Warning");
        else BalanceButton.ClearValue(ForegroundProperty);
        BalanceButton.ToolTip = (tooken ? Tr("Баланс и расход", "Balance and usage") : text) + " (Ctrl+B)";
        AutomationProperties.SetName(BalanceButton, provider switch
        {
            AgentProvider.ChatGpt when warning => text + Tr(": подписка сейчас ограничена", ": subscription is currently limited"),
            AgentProvider.Groq when warning => text + Tr(" почти исчерпаны", " are nearly exhausted"),
            AgentProvider.Tooken when snapshot != null => Tr($"Баланс {Tokens.Format(snapshot.Balance)} токенов", $"Balance: {Tokens.Format(snapshot.Balance)} tokens") + (warning ? Tr(", лимит расхода исчерпан", ", usage limit reached") : ""),
            _ => text,
        } + Tr(". Открыть подробности", ". Open details"));
    }

    private static bool Low(long? left, long? limit) => left != null && limit is > 0 && left < limit * 0.1;

    private void Balance_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsOpen) return;
        if (FlyoutShows(UsageCard)) CloseFlyout();
        else if (_provider == null) ShowToast(NotReadyText(), ToastKind.Warning);
        else if (_config.Provider == AgentProvider.Tooken && _usageUnavailable) ShowToast(Tr("Этот сервер не сообщает баланс и расход", "This provider does not report balance or usage"));
        else
        {
            BuildUsagePanel();
            ShowFlyout(UsageCard, BalanceButton);
            if (_usage.Last is not { } last || DateTime.Now - last.RefreshedAt > TimeSpan.FromSeconds(30)) _ = RefreshUsageAsync();
        }
    }

    private void UpdateContextBanner()
    {
        if (ContextUsage() is >= ContextWarning and var used)
            ShowBanner("context", Glyphs.Warning, Tr("Память модели почти заполнена", "Model context is nearly full"),
                Tr($"Переписка занимает {Tokens.Percent(used)} памяти модели. Начните новый чат, чтобы агент не терял начало разговора.", $"This chat uses {Tokens.Percent(used)} of the model’s context. Start a new chat to free up context."), Tr("Новый чат", "New chat"), StartNewChat, warning: true);
        else HideBanner("context");
    }

    private double? ContextUsage() =>
        _agent.Totals().ContextTokens is > 0 and var context && CurrentModel() is { } model && _models.TryGetValue(model, out var info) && info.ContextTokens is > 0 and var window
            ? (double)context / window
            : null;

    private void BuildUsagePanel()
    {
        UsagePanel.Children.Clear();
        if (_provider is ChatGptClient chat) BuildPlanPanel(chat);
        else if (_provider is ChatApiClient groq && _config.Provider == AgentProvider.Groq) BuildGroqPanel(groq.Limits);
        else if (_config.Provider == AgentProvider.Tooken) BuildBalancePanel();
        else
        {
            AddHeader(Tr("Расход ", "Usage for ") + ProviderName(_config.Provider));
            if (!AddPlaceholder(_agent.Totals() is { PromptTokens: 0, CompletionTokens: 0 }, Tr("Расход появится после первого ответа модели.", "Usage will appear after the first model response.")))
                AddChatSection();
        }
        AddRunTimeline();
    }

    void AddRunTimeline()
    {
        if (AgentProgressManager.Last(_toolContext, _agent.ChatPath) is not { } run) return;
        int calls = run.Operations.Count(step => step.Model != null);
        double seconds = (run.Ended - run.Started).TotalSeconds;
        var details = new StackPanel();
        foreach (var step in run.Operations.Where(step => step.Model != null || step.Step > run.Operations.Length - 36))
        {
            var name = step.Name switch
            {
                "start" => Tr("Подготовка", "Preparing"),
                "context" => Tr("Подготовка контекста", "Preparing context"),
                "response" => Tr("Запрос модели", "Model request"),
                "local_model" => Tr("Запрос локальной модели", "Local model request"),
                "title" => Tr("Название чата", "Chat title"),
                "compaction" => Tr("Сжатие истории", "History compaction"),
                _ => Activity(step.Name, "{}"),
            };
            var status = step.State switch
            {
                "completed" => Tr("готово", "done"),
                "paused" => Tr("пауза", "paused"),
                "failed" => Tr("ошибка", "failed"),
                "cancelled" => Tr("отменено", "cancelled"),
                "warning" => Tr("предупреждение", "warning"),
                _ => Tr("в работе", "running"),
            };
            var duration = step.Ended is { } ended ? $" · {(ended - step.Started).TotalSeconds:F1} с" : "";
            var usage = step.Usage is { } actual ? Tr(
                $" · вход {Tokens.Format(actual.PromptTokens)}, без кэша {Tokens.Format(Math.Max(0, actual.PromptTokens - actual.CachedTokens))}, кэш {Tokens.Format(actual.CachedTokens)}, выход {Tokens.Format(actual.CompletionTokens)}, рассуждение {Tokens.Format(actual.ReasoningTokens)}",
                $" · input {Tokens.Format(actual.PromptTokens)}, uncached {Tokens.Format(Math.Max(0, actual.PromptTokens - actual.CachedTokens))}, cached {Tokens.Format(actual.CachedTokens)}, output {Tokens.Format(actual.CompletionTokens)}, reasoning {Tokens.Format(actual.ReasoningTokens)}") :
                step.EstimatedInput > 0 ? Tr($" · вход ≈{Tokens.Format(step.EstimatedInput)}", $" · input ≈{Tokens.Format(step.EstimatedInput)}") : "";
            var retries = step.Retries > 0 ? Tr($" · повторов {step.Retries}", $" · retries {step.Retries}") : "";
            var images = step.Images > 0 ? Tr($" · изображений {step.Images}", $" · images {step.Images}") : "";
            var model = step.Model != null ? " · " + (step.Input?.Provider ?? run.Provider) + " · " + ModelDisplayName(step.Model) : "";
            var line = Caption($"{step.Step}. {name}{model} · {status}{duration}{usage}{images}{retries}", 4);
            line.ToolTip = step.Error;
            details.Children.Add(line);
            if (step.Input is { } input)
            {
                var parts = Tr(
                    $"Оценка входа ≈{input.Total:N0}: системный промпт {input.System:N0}, схемы {input.Tools:N0}, история {input.History:N0}, проект {input.Project:N0}, результаты {input.Results:N0}, изображения {input.Attachments:N0}. Совпавший префикс {input.StablePrefix:N0}, повторные результаты {input.RepeatedResults:N0}.",
                    $"Estimated input ≈{input.Total:N0}: system prompt {input.System:N0}, tool schemas {input.Tools:N0}, history {input.History:N0}, project {input.Project:N0}, tool results {input.Results:N0}, images {input.Attachments:N0}. Matching prefix {input.StablePrefix:N0}, repeated results {input.RepeatedResults:N0}.");
                details.Children.Add(Caption(parts, 0));
                var change = input.PrefixChange switch
                {
                    "first_request" => Tr("первый запрос", "first request"),
                    "client_changed" => Tr("сменился клиент API", "API client changed"),
                    "provider_changed" => Tr("сменился провайдер", "provider changed"),
                    "model_changed" => Tr("сменилась модель", "model changed"),
                    "tools_changed" => Tr("сменились схемы инструментов", "tool schemas changed"),
                    "options_changed" => Tr("сменились параметры модели", "model options changed"),
                    "tool_output_changed" => Tr("изменился вывод инструмента", "tool output changed"),
                    "system_changed" => Tr("изменился системный промпт", "system prompt changed"),
                    "history_changed" => Tr("изменилась история или контекст", "history or context changed"),
                    "history_shortened" => Tr("история стала короче", "history shortened"),
                    _ => Tr("префикс сохранился", "prefix held"),
                };
                if (input.SchemaVersion is { } version)
                    details.Children.Add(Caption(Tr(
                        $"Локальный префикс · {change} · схемы {version} · повторных результатов в префиксе {input.RepeatedInPrefix:N0}.",
                        $"Local prefix · {change} · schemas {version} · repeated tool output in prefix {input.RepeatedInPrefix:N0}."), 0));
            }
            if (step.Name == "compaction" && step.State == "cancelled")
            {
                var reason = step.Error switch
                {
                    "user_cancelled" => Tr("пользователь остановил задачу", "the user stopped the task"),
                    "turn_ended" => Tr("основной запрос завершился", "the main run ended"),
                    "compaction_timeout" => Tr("истёк таймаут сжатия", "compaction timed out"),
                    "provider_cancelled" => Tr("соединение с провайдером прервано", "the provider connection was interrupted"),
                    _ => Tr("причина отмены недоступна", "cancellation reason unavailable"),
                };
                details.Children.Add(Caption(Tr($"Сжатие отменено: {reason}. Провайдер не вернул данные о расходе.",
                    $"Compaction cancelled: {reason}. The provider returned no usage data."), 0));
            }
        }
        var title = Tr($"Последний запуск · запросов к моделям {calls} · повторов {run.Retries} · {seconds:F0} с",
            $"Last run · {calls} model requests · {run.Retries} retries · {seconds:F0} s");
        var view = new Expander { Header = title, Style = Theme.Style("ProgressSection"),
            Content = new ScrollViewer { MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = details } };
        AutomationProperties.SetName(view, title);
        AddUsage(Divider(), view);
    }

    private void AddUsage(params UIElement[] items)
    {
        foreach (var item in items) UsagePanel.Children.Add(item);
    }

    private void AddHeader(string title, bool refreshable = false)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (refreshable)
        {
            var refresh = Theme.IconButton(Glyphs.Refresh, Tr("Обновить баланс и расход", "Refresh balance and usage"), 28, 14);
            refresh.IsEnabled = !UsageLoading;
            refresh.Margin = new Thickness(0, -6, -6, -6);
            refresh.Click += (_, _) =>
            {
                _ = RefreshUsageAsync();
                BuildUsagePanel();
            };
            DockPanel.SetDock(refresh, Dock.Right);
            header.Children.Add(refresh);
        }
        header.Children.Add(Caption(title));
        AddUsage(header);
    }

    private bool AddPlaceholder(bool empty, string waiting)
    {
        if (!empty) return false;
        AddUsage(UsageNote(UsageLoading ? Tr("Загружаю данные…", "Loading data…") : _usageError != null ? Tr("Не удалось получить данные: ", "Could not load data: ") + _usageError : waiting,
            _usageError != null && !UsageLoading ? "DangerText" : "TextSoft"));
        AddChatSection();
        return true;
    }

    private static TextBlock UsageNote(string text, string foreground) =>
        new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) }.Paint(TextBlock.ForegroundProperty, foreground);

    private void BuildBalancePanel()
    {
        AddHeader(Tr("Баланс tooken.club", "tooken.club balance"), true);
        var snapshot = _usage.Last;
        if (AddPlaceholder(snapshot == null, Tr("Данных пока нет.", "No data yet."))) return;
        AddUsage(new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            Inlines =
            {
                new Run(Tokens.Format(snapshot!.Balance)) { FontSize = 20, FontWeight = FontWeights.SemiBold },
                new Run(Tr(" токенов", " tokens")) { FontSize = 13 }.Paint(TextElement.ForegroundProperty, "TextDim"),
            },
        });
        if (snapshot.FullBalance > 0 && snapshot.FullBalance > snapshot.Balance)
        {
            double left = (double)snapshot.Balance / snapshot.FullBalance;
            AddUsage(Meter(left, left < 0.1),
                Caption(Tr($"Осталось {Tokens.Percent(left)} от {Tokens.Format(snapshot.FullBalance)} (баланс после пополнения)", $"{Tokens.Percent(left)} of {Tokens.Format(snapshot.FullBalance)} remaining since your last top-up"), 4));
        }
        AddUsage(Divider(), Row(Tr("Потрачено сегодня", "Used today"), Tokens.Format(snapshot.Today)), Row(Tr("За 7 дней", "Last 7 days"), Tokens.Format(snapshot.Week)),
            Row(Tr("За 30 дней", "Last 30 days"), (snapshot.HistoryComplete ? "" : Tr("не меньше ", "at least ")) + Tokens.Format(snapshot.Month)));
        if (snapshot.Week > 0 && snapshot.Balance > 0)
        {
            int days = (int)Math.Min(9999, Math.Floor(snapshot.Balance / (snapshot.Week / 7.0)));
            AddUsage(Caption(days < 1 ? Tr("При расходе как за последнюю неделю баланса не хватит и на день", "At last week’s rate the balance will last less than one day.")
                : Tr($"При расходе как за последнюю неделю хватит примерно на {Plural(days, "день", "дня", "дней")}", $"At last week’s rate the balance will last about {days} {(days == 1 ? "day" : "days")}."), 4));
        }
        AddChatSection();
        AddUsage(Divider());
        if (_config.DayLimit == null && _config.MonthLimit == null)
            AddUsage(new TextBlock
            {
                FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Inlines = { new Run(Tr("Свои лимиты на день и месяц не заданы. ", "Daily and monthly limits are unset. ")), MakeLink(Tr("Задать лимиты", "Set limits"), () => OpenSettings(DayLimitBox)) },
            }.Paint(TextBlock.ForegroundProperty, "TextSoft"));
        else
        {
            if (_config.DayLimit is { } day) AddLimit(Tr("Лимит на день", "Daily limit"), snapshot.Today, day);
            if (_config.MonthLimit is { } month) AddLimit(Tr("Лимит на месяц", "Monthly limit"), snapshot.MonthToDate, month);
        }
        AddFooter(snapshot.RefreshedAt, Tr("Пополнить в кабинете", "Top up in dashboard"), "https://tooken.club/dashboard");
    }

    private void BuildPlanPanel(ChatGptClient chat)
    {
        AddHeader(Tr("Подписка ChatGPT", "ChatGPT subscription"));
        if (_chatGpt.Label is { } label) AddUsage(Caption(label, bottom: 8));
        bool allowed = _chatGpt.PlanAllowed;
        AddUsage(Row(Tr("Использование подписки", "Subscription access"), allowed ? Tr("разрешено", "Granted") : Tr("не разрешено", "Denied"), warning: !allowed),
            UsageNote(chat.LimitMessage ?? Tr("Сколько подписки уходит на Sharp Agent и какие у него лимиты, показывают настройки ChatGPT.", "Open ChatGPT settings to see Sharp Agent’s subscription usage and limits."),
                chat.LimitMessage != null ? "Warning" : "TextSoft"));
        AddChatSection();
        AddFooter(null, Tr("Управлять использованием", "Manage usage"), ChatGptClient.UsageUrl);
    }

    private void BuildGroqPanel(GroqLimits? limits)
    {
        AddHeader(Tr("Лимиты Groq", "Groq limits"));
        if (AddPlaceholder(limits == null, Tr("Лимиты появятся после первого ответа модели.", "Limits will appear after the first model response."))) return;
        AddQuota(Tr("Запросов в сутки", "Requests per day"), limits!.RequestsLeft, limits.Requests, limits.RequestsReset);
        AddQuota(Tr("Токенов в минуту", "Tokens per minute"), limits.TokensLeft, limits.Tokens, limits.TokensReset);
        AddChatSection();
        AddFooter(limits.Updated, Tr("Консоль Groq", "Groq console"), "https://console.groq.com");
    }

    private void AddQuota(string title, long? left, long? limit, DateTime? reset)
    {
        if (left is not { } remaining || limit is not > 0) return;
        bool low = Low(left, limit);
        AddUsage(Row(title, Tr($"осталось {Tokens.Format(remaining)} из {Tokens.Format(limit.Value)}", $"{Tokens.Format(remaining)} of {Tokens.Format(limit.Value)} remaining"), warning: low), Meter((double)remaining / limit.Value, low),
            Caption(reset - DateTime.Now is { } time && time > TimeSpan.Zero ? Tr("Восстановится через ", "Resets in ") + Tokens.Duration(time) : "", 4, bottom: 8));
    }

    private void AddChatSection()
    {
        var totals = _agent.Totals();
        var model = CurrentModel();
        long chatTokens = totals.PromptTokens + totals.CompletionTokens;
        bool tooken = _config.Provider == AgentProvider.Tooken;
        var rate = tooken && model != null ? _usage.RateOf(model) : null;
        if (chatTokens == 0 && rate == null) return;
        AddUsage(Divider());
        if (chatTokens > 0)
        {
            AddUsage(Row(Tr("Этот чат", "This chat"), Tokens.Format(chatTokens) + Tr(" токенов", " tokens")));
            if (totals.Requests > 0)
                AddUsage(Caption(Tr($"Вход: {Tokens.Format(totals.PromptTokens)} · Выход: {Tokens.Format(totals.CompletionTokens)} · Учтено запросов: {totals.Requests}",
                    $"Input: {Tokens.Format(totals.PromptTokens)} · Output: {Tokens.Format(totals.CompletionTokens)} · Requests recorded: {totals.Requests}"), 2));
            if (totals.CachedTokens > 0)
                AddUsage(Caption(Tr($"Вход из кэша: {Tokens.Format(totals.CachedTokens)}", $"Cached input: {Tokens.Format(totals.CachedTokens)}"), 2));
            if (totals.ReasoningTokens > 0)
                AddUsage(Caption(Tr($"Из выхода на рассуждение: {Tokens.Format(totals.ReasoningTokens)}", $"Reasoning output: {Tokens.Format(totals.ReasoningTokens)}"), 2));
            if (tooken && totals.TokensByModel.Where(pair => pair.Key.Provider == AgentProvider.Tooken).Sum(pair => pair.Value * (_usage.RateOf(pair.Key.Model) ?? 0)) is >= 1 and var charged)
                AddUsage(Caption(Tr($"С баланса за него списано около {Tokens.Format((long)Math.Round(charged))}", $"Charged about {Tokens.Format((long)Math.Round(charged))} tokens"), 2));
        }
        if (model != null && ContextUsage() is { } used && _models.TryGetValue(model, out var info))
        {
            bool full = used >= ContextWarning;
            AddUsage(Row(Tr("Память модели", "Model context"), Tr("занято ", "used ") + Tokens.Percent(used), topMargin: 8), Meter(used, full),
                Caption($"{Tokens.Format(totals.ContextTokens)} из {Tokens.Format(info.ContextTokens ?? 0)} токенов." + (full ? Tr(" Скоро чат перестанет помещаться — начните новый.", " Start a new chat to free up context.") : ""), 4));
        }
        if (rate is { } factor)
        {
            var shown = factor.ToString("0.##", UiLanguage.Culture);
            var row = Row(Tr("Коэффициент модели", "Model multiplier"), "×" + shown, topMargin: 8);
            row.ToolTip = Tr($"За один токен запроса к {model} с баланса списывается {shown} токена.", $"Each input token sent to {model} costs {shown} tokens.");
            AddUsage(row);
        }
    }

    private void AddFooter(DateTime? updated, string linkText, string url)
    {
        AddUsage(Divider());
        if (updated is { } time)
            AddUsage(Caption(UsageLoading ? Tr("Обновляю…", "Refreshing…") : _usageError != null ? Tr("Не обновилось: ", "Could not refresh: ") + _usageError : Tr("Обновлено в ", "Updated at ") + time.ToString("HH:mm", UiLanguage.Culture), bottom: 4));
        // Links are rows whose hover fill reaches the card edge, so the text still lines up with the rest.
        var link = new Button { Style = Theme.Style("RowButton"), Content = linkText, Height = 30, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(-8, 0, -8, 0) };
        link.Click += (_, _) => FileActions.OpenUrl(url);
        AddUsage(link);
    }

    private void AddLimit(string title, long spent, long limit)
    {
        double left = 1 - (double)spent / limit;
        bool reached = spent >= limit;
        AddUsage(Row(title, reached ? Tr("исчерпан", "Reached") : Tr("осталось ", "remaining ") + Tokens.Percent(left), warning: reached),
            Meter(left, false), Caption(Tr($"Израсходовано {Tokens.Format(spent)} из {Tokens.Format(limit)}", $"{Tokens.Format(spent)} of {Tokens.Format(limit)} used"), 4, bottom: 8));
    }

    private static TextBlock Caption(string text, double top = 0, double bottom = 0) => new TextBlock
    {
        Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, top, 0, bottom),
    }.Paint(TextBlock.ForegroundProperty, "TextDim");

    private static Grid Row(string label, string value, double topMargin = 0, bool warning = false)
    {
        var right = new TextBlock { Text = value, Margin = new Thickness(12, 0, 0, 0) }.Paint(TextBlock.ForegroundProperty, warning ? "Warning" : "Text");
        Grid.SetColumn(right, 1);
        return new()
        {
            Margin = new Thickness(0, topMargin, 0, 8), Background = Brushes.Transparent,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { new TextBlock { Text = label }.Paint(TextBlock.ForegroundProperty, "TextSoft"), right },
        };
    }

    private static ProgressBar Meter(double fraction, bool low) =>
        new ProgressBar { Style = Theme.Style("Meter"), Value = Math.Clamp(fraction, 0, 1) * 100 }.Paint(ForegroundProperty, low ? "Warning" : "Text");

    private static Border Divider() => new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) }.Paint(Border.BackgroundProperty, "LineStrong");

    private static string Plural(int number, string one, string few, string many)
    {
        int n = Math.Abs(number) % 100, last = n % 10;
        return $"{number} {(n is >= 11 and <= 14 ? many : last == 1 ? one : last is >= 2 and <= 4 ? few : many)}";
    }

    private string? LimitReached(SpendSnapshot snapshot) =>
        _config.DayLimit is { } day && snapshot.Today >= day
            ? Tr($"За сегодня израсходовано {Tokens.Format(snapshot.Today)} токенов при лимите {Tokens.Format(day)} в день.", $"Used {Tokens.Format(snapshot.Today)} tokens today. Daily limit: {Tokens.Format(day)}.")
            : _config.MonthLimit is { } month && snapshot.MonthToDate >= month
                ? Tr($"С начала месяца израсходовано {Tokens.Format(snapshot.MonthToDate)} токенов при лимите {Tokens.Format(month)} в месяц.", $"Used {Tokens.Format(snapshot.MonthToDate)} tokens this month. Monthly limit: {Tokens.Format(month)}.")
                : null;

    private async Task<bool> CheckLimitsAsync(AgentRunner agent)
    {
        var run = _runs[agent];
        if (run.LimitDeclined) return false;
        if (_limitAsk is { } pending)
        {
            bool allowed = await pending;
            run.LimitDeclined = !allowed;
            return allowed;
        }
        var reached = _config.Provider == AgentProvider.Tooken && _usage.Last is { } snapshot ? LimitReached(snapshot) : null;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (reached == null || _state.LimitOverrideDay == today) return true;
        var permission = new ConfirmRequest(Tr("Продолжить работу после достижения лимита расхода", "Continue after reaching the usage limit"),
            "", Tr("Продолжить", "Continue"));
        run.VoicePermission = permission;
        run.VoicePermissionAnnounced = false;
        if (run.Extra == null && (_agent != agent || !ActiveChatWindow.IsVisible))
        {
            try { await WaitForActiveChatAsync(agent, run.Cancellation.Token); }
            finally { run.VoicePermission = null; }
            return await CheckLimitsAsync(agent);
        }
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _limitAsk = answer.Task;
        bool proceed = false;
        try
        {
            SetTaskbar(TaskbarItemProgressState.Paused);
            var owner = ActiveChatWindow;
            WindowEffects.Flash(owner);
            proceed = DarkDialog.Ask(owner, Tr("Лимит расхода исчерпан", "Usage limit reached"),
                reached + "\n\nПродолжить работу агента? До конца дня программа больше не будет об этом спрашивать. Изменить лимиты можно в настройках.", Tr("Продолжить", "Continue"), Tr("Остановить", "Stop"));
            SetTaskbar(TaskbarItemProgressState.Indeterminate);
            if (proceed)
            {
                _state.LimitOverrideDay = today;
                _state.Save();
            }
            else run.LimitDeclined = true;
        }
        finally
        {
            run.VoicePermission = null;
            _limitAsk = null;
            answer.SetResult(proceed);
        }
        return proceed;
    }
}
