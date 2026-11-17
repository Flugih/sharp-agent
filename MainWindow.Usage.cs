using System.Windows.Documents;
using System.Windows.Shell;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    private const double ContextWarning = 0.85;

    private bool _usageUnavailable;
    private object? _usageLoading;
    private bool _limitDeclined;
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
        catch (Exception ex) { error = ex is OperationCanceledException ? "сервер не ответил вовремя" : ex.Message.Split('\n')[0]; }
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
            AgentProvider.Tooken => snapshot != null ? "Баланс " + Tokens.Format(snapshot.Balance) : "Баланс и расход",
            AgentProvider.ChatGpt or AgentProvider.Groq => "Лимиты " + ProviderName(provider),
            _ => "Расход " + ProviderName(provider),
        };
        BalanceButton.Visibility = VisibleIf(!(tooken && _usageUnavailable));
        BalanceText.Text = text;
        if (warning) BalanceButton.Foreground = Theme.Warning;
        else BalanceButton.ClearValue(ForegroundProperty);
        BalanceButton.ToolTip = (tooken ? "Баланс и расход" : text) + " (Ctrl+B)";
        AutomationProperties.SetName(BalanceButton, provider switch
        {
            AgentProvider.ChatGpt when warning => text + ": подписка сейчас ограничена",
            AgentProvider.Groq when warning => text + " почти исчерпаны",
            AgentProvider.Tooken when snapshot != null => $"Баланс {Tokens.Format(snapshot.Balance)} токенов{(warning ? ", лимит расхода исчерпан" : "")}",
            _ => text,
        } + ". Открыть подробности");
    }

    private static bool Low(long? left, long? limit) => left != null && limit is > 0 && left < limit * 0.1;

    private void Balance_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow != null) return;
        if (FlyoutShows(UsageCard)) CloseFlyout();
        else if (_provider == null) ShowToast(NotReadyText(), ToastKind.Warning);
        else if (_config.Provider == AgentProvider.Tooken && _usageUnavailable) ShowToast("Этот сервер не сообщает баланс и расход");
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
            ShowBanner("context", Glyphs.Warning, "Память модели почти заполнена",
                $"Переписка занимает {Tokens.Percent(used)} памяти модели. Начните новый чат, чтобы агент не терял начало разговора.", "Новый чат", StartNewChat, warning: true);
        else HideBanner("context");
    }

    private double? ContextUsage() =>
        _agent.Totals().ContextTokens is > 0 and var context && _models.TryGetValue(CurrentModel(), out var info) && info.ContextTokens is > 0 and var window
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
            AddHeader("Расход " + ProviderName(_config.Provider));
            if (!AddPlaceholder(_agent.Totals().PromptTokens + _agent.Totals().CompletionTokens == 0, "Расход появится после первого ответа модели."))
                AddChatSection();
        }
    }

    private void AddUsage(params UIElement[] items)
    {
        foreach (var item in items) UsagePanel.Children.Add(item);
    }

    private void AddHeader(string title, bool refreshable = false)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        if (refreshable)
        {
            var refresh = Theme.IconButton(Glyphs.Refresh, "Обновить баланс и расход", 26);
            refresh.IsEnabled = !UsageLoading;
            refresh.Margin = new Thickness(0, -4, -6, 0);
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
        AddUsage(UsageNote(UsageLoading ? "Загружаю данные…" : _usageError != null ? "Не удалось получить данные: " + _usageError : waiting,
            _usageError != null && !UsageLoading ? Theme.DangerText : Theme.TextSoft));
        AddChatSection();
        return true;
    }

    private static TextBlock UsageNote(string text, Brush foreground) =>
        new() { Text = text, FontSize = 13, Foreground = foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };

    private void BuildBalancePanel()
    {
        AddHeader("Баланс tooken.club", true);
        var snapshot = _usage.Last;
        if (AddPlaceholder(snapshot == null, "Данных пока нет.")) return;
        AddUsage(new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            Inlines =
            {
                new Run(Tokens.Format(snapshot!.Balance)) { FontSize = 20, FontWeight = FontWeights.SemiBold },
                new Run(" токенов") { FontSize = 13, Foreground = Theme.TextMuted },
            },
        });
        if (snapshot.FullBalance > 0 && snapshot.FullBalance > snapshot.Balance)
        {
            double left = (double)snapshot.Balance / snapshot.FullBalance;
            AddUsage(Meter(left, left < 0.1 ? Theme.Warning : Theme.Accent),
                Caption($"Осталось {Tokens.Percent(left)} от {Tokens.Format(snapshot.FullBalance)} (баланс после пополнения)", 4));
        }
        AddUsage(Divider(), Row("Потрачено сегодня", Tokens.Format(snapshot.Today)), Row("За 7 дней", Tokens.Format(snapshot.Week)),
            Row("За 30 дней", (snapshot.HistoryComplete ? "" : "не меньше ") + Tokens.Format(snapshot.Month)));
        if (snapshot.Week > 0 && snapshot.Balance > 0)
        {
            int days = (int)Math.Min(9999, Math.Floor(snapshot.Balance / (snapshot.Week / 7.0)));
            AddUsage(Caption(days < 1 ? "При расходе как за последнюю неделю баланса не хватит и на день"
                : $"При расходе как за последнюю неделю хватит примерно на {Plural(days, "день", "дня", "дней")}", 4));
        }
        AddChatSection();
        AddUsage(Divider());
        if (_config.DayLimit == null && _config.MonthLimit == null)
            AddUsage(new TextBlock
            {
                FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Theme.TextSoft,
                Inlines = { new Run("Свои лимиты на день и месяц не заданы. "), MakeLink("Задать лимиты", () => OpenSettings(DayLimitBox)) },
            });
        else
        {
            if (_config.DayLimit is { } day) AddLimit("Лимит на день", snapshot.Today, day);
            if (_config.MonthLimit is { } month) AddLimit("Лимит на месяц", snapshot.MonthToDate, month);
        }
        AddFooter(snapshot.RefreshedAt, "Пополнить в кабинете", "https://tooken.club/dashboard");
    }

    private void BuildPlanPanel(ChatGptClient chat)
    {
        AddHeader("Подписка ChatGPT");
        if (_chatGpt.Label is { } label) AddUsage(Caption(label, bottom: 8));
        bool allowed = _chatGpt.PlanAllowed;
        AddUsage(Row("Использование подписки", allowed ? "разрешено" : "не разрешено", valueBrush: allowed ? null : Theme.Warning),
            UsageNote(chat.LimitMessage ?? "Сколько подписки уходит на Sharp Agent и какие у него лимиты, показывают настройки ChatGPT.",
                chat.LimitMessage != null ? Theme.Warning : Theme.TextSoft));
        AddChatSection();
        AddFooter(null, "Управлять использованием", ChatGptClient.UsageUrl);
    }

    private void BuildGroqPanel(GroqLimits? limits)
    {
        AddHeader("Лимиты Groq");
        if (AddPlaceholder(limits == null, "Лимиты появятся после первого ответа модели.")) return;
        AddQuota("Запросов в сутки", limits!.RequestsLeft, limits.Requests, limits.RequestsReset);
        AddQuota("Токенов в минуту", limits.TokensLeft, limits.Tokens, limits.TokensReset);
        AddChatSection();
        AddFooter(limits.Updated, "Консоль Groq", "https://console.groq.com");
    }

    private void AddQuota(string title, long? left, long? limit, DateTime? reset)
    {
        if (left is not { } remaining || limit is not > 0) return;
        var brush = Low(left, limit) ? Theme.Warning : null;
        AddUsage(Row(title, $"осталось {Tokens.Format(remaining)} из {Tokens.Format(limit.Value)}", valueBrush: brush), Meter((double)remaining / limit.Value, brush ?? Theme.Accent),
            Caption(reset - DateTime.Now is { } time && time > TimeSpan.Zero ? "Восстановится через " + Tokens.Duration(time) : "", 4, bottom: 8));
    }

    private void AddChatSection()
    {
        var totals = _agent.Totals();
        var model = CurrentModel();
        long chatTokens = totals.PromptTokens + totals.CompletionTokens;
        bool tooken = _config.Provider == AgentProvider.Tooken;
        var rate = tooken ? _usage.RateOf(model) : null;
        if (chatTokens == 0 && rate == null) return;
        AddUsage(Divider());
        if (chatTokens > 0)
        {
            AddUsage(Row("Этот чат", Tokens.Format(chatTokens) + " токенов"));
            if (tooken && totals.TokensByModel.Where(pair => pair.Key.Provider == AgentProvider.Tooken).Sum(pair => pair.Value * (_usage.RateOf(pair.Key.Model) ?? 0)) is >= 1 and var charged)
                AddUsage(Caption($"С баланса за него списано около {Tokens.Format((long)Math.Round(charged))}", 2));
        }
        if (ContextUsage() is { } used && _models.TryGetValue(model, out var info))
        {
            bool full = used >= ContextWarning;
            AddUsage(Row("Память модели", "занято " + Tokens.Percent(used), topMargin: 8), Meter(used, full ? Theme.Warning : Theme.Accent),
                Caption($"{Tokens.Format(totals.ContextTokens)} из {Tokens.Format(info.ContextTokens ?? 0)} токенов." + (full ? " Скоро чат перестанет помещаться — начните новый." : ""), 4));
        }
        if (rate is { } factor)
        {
            var shown = factor.ToString("0.##", Ru);
            var row = Row("Коэффициент модели", "×" + shown, topMargin: 8);
            row.ToolTip = $"За один токен запроса к {model} с баланса списывается {shown} токена.";
            AddUsage(row);
        }
    }

    private void AddFooter(DateTime? updated, string linkText, string url)
    {
        var cabinet = new TextBlock { FontSize = 13, Inlines = { MakeLink(linkText, () => FileActions.OpenUrl(url)) } };
        DockPanel.SetDock(cabinet, Dock.Right);
        var footer = new DockPanel { LastChildFill = false, Children = { cabinet } };
        if (updated is { } time)
            footer.Children.Add(Caption(UsageLoading ? "Обновляю…" : _usageError != null ? "Не обновилось: " + _usageError : "Обновлено в " + time.ToString("HH:mm", Ru)));
        AddUsage(Divider(), footer);
    }

    private void AddLimit(string title, long spent, long limit)
    {
        double left = 1 - (double)spent / limit;
        bool reached = spent >= limit;
        AddUsage(Row(title, reached ? "исчерпан" : "осталось " + Tokens.Percent(left), valueBrush: reached ? Theme.Warning : null),
            Meter(left, Theme.Accent), Caption($"Израсходовано {Tokens.Format(spent)} из {Tokens.Format(limit)}", 4, bottom: 8));
    }

    private static TextBlock Caption(string text, double top = 0, double bottom = 0) => new()
    {
        Text = text, FontSize = 12, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, top, 0, bottom),
    };

    private static Grid Row(string label, string value, double topMargin = 0, Brush? valueBrush = null)
    {
        var right = new TextBlock { Text = value, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = valueBrush ?? Theme.Text, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        return new()
        {
            Margin = new Thickness(0, topMargin, 0, 6), Background = Brushes.Transparent,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { new TextBlock { Text = label, FontSize = 13, Foreground = Theme.TextSoft }, right },
        };
    }

    private static ProgressBar Meter(double fraction, Brush fill) => new() { Style = Theme.Style("Meter"), Value = Math.Clamp(fraction, 0, 1) * 100, Foreground = fill };

    private static Border Divider() => new() { Height = 1, Background = Theme.LineStrong, Margin = new Thickness(0, 10, 0, 10) };

    private static string Plural(int number, string one, string few, string many)
    {
        int n = Math.Abs(number) % 100, last = n % 10;
        return $"{number} {(n is >= 11 and <= 14 ? many : last == 1 ? one : last is >= 2 and <= 4 ? few : many)}";
    }

    private string? LimitReached(SpendSnapshot snapshot) =>
        _config.DayLimit is { } day && snapshot.Today >= day
            ? $"За сегодня израсходовано {Tokens.Format(snapshot.Today)} токенов при лимите {Tokens.Format(day)} в день."
            : _config.MonthLimit is { } month && snapshot.MonthToDate >= month
                ? $"С начала месяца израсходовано {Tokens.Format(snapshot.MonthToDate)} токенов при лимите {Tokens.Format(month)} в месяц."
                : null;

    private Task<bool> CheckLimitsAsync()
    {
        if (_limitAsk != null) return _limitAsk;
        if (_limitDeclined) return Task.FromResult(false);
        var reached = _config.Provider == AgentProvider.Tooken && _usage.Last is { } snapshot ? LimitReached(snapshot) : null;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (reached == null || _state.LimitOverrideDay == today) return Task.FromResult(true);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _limitAsk = answer.Task;
        bool proceed = false;
        try
        {
            SetTaskbar(TaskbarItemProgressState.Paused);
            var owner = ActiveChatWindow;
            WindowEffects.Flash(owner);
            proceed = DarkDialog.Ask(owner, "Лимит расхода исчерпан",
                reached + "\n\nПродолжить работу агента? До конца дня программа больше не будет об этом спрашивать. Изменить лимиты можно в настройках.", "Продолжить", "Остановить");
            SetTaskbar(TaskbarItemProgressState.Indeterminate);
            if (proceed)
            {
                _state.LimitOverrideDay = today;
                _state.Save();
            }
            else _limitDeclined = true;
        }
        finally
        {
            _limitAsk = null;
            answer.SetResult(proceed);
        }
        return answer.Task;
    }
}
