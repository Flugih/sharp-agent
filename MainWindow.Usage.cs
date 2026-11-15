using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shell;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Ui;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    private const double ContextWarning = 0.85;

    private bool _usageUnavailable;
    private object? _usageLoading;
    private bool _limitDeclined;
    private string? _usageError;

    private bool UsageLoading => _usageLoading != null && ReferenceEquals(_usageLoading, _provider);

    private async Task RefreshUsageAsync()
    {
        var tooken = _tooken;
        if (tooken == null || !ReferenceEquals(_provider, tooken) || _usageUnavailable || ReferenceEquals(_usageLoading, tooken)) return;
        _usageLoading = tooken;
        string? error = null;
        bool unavailable = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await _usage.RefreshAsync(tooken, UsageTracker.KeyId(_config.ApiKey!, _config.BaseUrl), cts.Token);
        }
        catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            unavailable = true;
        }
        catch (OperationCanceledException)
        {
            error = "сервер не ответил вовремя";
        }
        catch (Exception ex)
        {
            error = ex.Message.Split('\n')[0];
        }

        if (!ReferenceEquals(tooken, _provider)) return;
        _usageLoading = null;
        _usageError = error;
        _usageUnavailable |= unavailable;
        UpdateBalanceIndicator();
        if (FlyoutShows(UsageCard)) BuildUsagePanel();
    }

    private void UpdateBalanceIndicator()
    {
        var (text, name, warning) = _config.Provider switch
        {
            AgentProvider.ChatGpt => PlanSummary(),
            AgentProvider.Groq => GroqSummary(),
            _ => BalanceSummary(),
        };
        BalanceButton.Visibility = _config.Provider == AgentProvider.Tooken && _usageUnavailable ? Visibility.Collapsed : Visibility.Visible;
        BalanceText.Text = text;
        if (warning) BalanceButton.Foreground = Theme.Warning;
        else BalanceButton.ClearValue(ForegroundProperty);
        BalanceButton.ToolTip = (_config.Provider == AgentProvider.Tooken ? "Баланс и расход" : "Лимиты " + ProviderName(_config.Provider)) +
                                " (Ctrl+B)";
        AutomationProperties.SetName(BalanceButton, name + ". Открыть подробности");
    }

    private (string Text, string Name, bool Warning) BalanceSummary()
    {
        if (_usage.Last is not { } snapshot) return ("Баланс и расход", "Баланс и расход", false);
        bool overLimit = LimitReached(snapshot) != null;
        return ("Баланс " + Tokens.Format(snapshot.Balance),
            $"Баланс {Tokens.Format(snapshot.Balance)} токенов{(overLimit ? ", лимит расхода исчерпан" : "")}", overLimit);
    }

    private (string Text, string Name, bool Warning) PlanSummary()
    {
        bool limited = (_provider as ChatGptClient)?.LimitMessage != null;
        return ("Лимиты ChatGPT", limited ? "Лимиты ChatGPT: подписка сейчас ограничена" : "Лимиты ChatGPT", limited);
    }

    private (string Text, string Name, bool Warning) GroqSummary()
    {
        var limits = (_provider as ChatApiClient)?.Limits;
        bool low = limits != null && (Low(limits.RequestsLeft, limits.Requests) || Low(limits.TokensLeft, limits.Tokens));
        return ("Лимиты Groq", low ? "Лимиты Groq почти исчерпаны" : "Лимиты Groq", low);
    }

    private static bool Low(long? left, long? limit) => left != null && limit is > 0 && left < limit * 0.1;

    private void Balance_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        if (FlyoutShows(UsageCard))
        {
            CloseFlyout();
            return;
        }
        if (_provider == null)
        {
            ShowToast(NotReadyText(), ToastKind.Warning);
            return;
        }
        if (_config.Provider == AgentProvider.Tooken && _usageUnavailable)
        {
            ShowToast("Этот сервер не сообщает баланс и расход");
            return;
        }

        BuildUsagePanel();
        ShowFlyout(UsageCard, BalanceButton);
        var updated = ReferenceEquals(_provider, _tooken) ? _usage.Last?.RefreshedAt : DateTime.Now;
        if (updated == null || DateTime.Now - updated > TimeSpan.FromSeconds(30)) _ = RefreshUsageAsync();
    }

    private void UpdateContextBanner()
    {
        if (ContextUsage() is >= ContextWarning and var used)
            ShowBanner("context", Glyphs.Warning, "Память модели почти заполнена",
                $"Переписка занимает {Tokens.Percent(used)} памяти модели. Начните новый чат, чтобы агент не терял начало разговора.",
                "Новый чат", StartNewChat, warning: true);
        else HideBanner("context");
    }

    private double? ContextUsage()
    {
        var context = _agent.Totals().ContextTokens;
        return context > 0 && _models.TryGetValue(CurrentModel(), out var info) && info.ContextTokens is > 0 and var window
            ? (double)context / window
            : null;
    }

    private void BuildUsagePanel()
    {
        UsagePanel.Children.Clear();
        switch (_provider)
        {
            case ChatGptClient chat:
                BuildPlanPanel(chat);
                break;
            case ChatApiClient groq when _config.Provider == AgentProvider.Groq:
                BuildGroqPanel(groq.Limits);
                break;
            default:
                BuildBalancePanel();
                break;
        }
    }

    private void AddHeader(string title, bool refreshable)
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
        UsagePanel.Children.Add(header);
    }

    private bool AddPlaceholder(bool empty, string waiting)
    {
        if (!empty) return false;
        UsagePanel.Children.Add(new TextBlock
        {
            Text = UsageLoading ? "Загружаю данные…" : _usageError != null ? "Не удалось получить данные: " + _usageError : waiting,
            FontSize = 13, Foreground = _usageError != null && !UsageLoading ? Theme.DangerText : Theme.TextSoft,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
        });
        AddChatSection();
        return true;
    }

    private void BuildBalancePanel()
    {
        AddHeader("Баланс tooken.club", refreshable: true);
        var snapshot = _usage.Last;
        if (AddPlaceholder(snapshot == null, "Данных пока нет.")) return;

        var balance = new TextBlock { Margin = new Thickness(0, 0, 0, 8) };
        balance.Inlines.Add(new Run(Tokens.Format(snapshot!.Balance)) { FontSize = 20, FontWeight = FontWeights.SemiBold });
        balance.Inlines.Add(new Run(" токенов") { FontSize = 13, Foreground = Theme.TextMuted });
        UsagePanel.Children.Add(balance);

        if (snapshot.FullBalance > 0 && snapshot.FullBalance > snapshot.Balance)
        {
            double left = (double)snapshot.Balance / snapshot.FullBalance;
            UsagePanel.Children.Add(Meter(left, left < 0.1 ? Theme.Warning : Theme.Accent));
            UsagePanel.Children.Add(Caption(
                $"Осталось {Tokens.Percent(left)} от {Tokens.Format(snapshot.FullBalance)} (баланс после пополнения)", 4));
        }

        UsagePanel.Children.Add(Divider());
        UsagePanel.Children.Add(Row("Потрачено сегодня", Tokens.Format(snapshot.Today)));
        UsagePanel.Children.Add(Row("За 7 дней", Tokens.Format(snapshot.Week)));
        UsagePanel.Children.Add(Row("За 30 дней", (snapshot.HistoryComplete ? "" : "не меньше ") + Tokens.Format(snapshot.Month)));
        if (snapshot.Week > 0 && snapshot.Balance > 0)
        {
            int days = (int)Math.Min(9999, Math.Floor(snapshot.Balance / (snapshot.Week / 7.0)));
            UsagePanel.Children.Add(Caption(days < 1
                ? "При расходе как за последнюю неделю баланса не хватит и на день"
                : $"При расходе как за последнюю неделю хватит примерно на {Plural(days, "день", "дня", "дней")}", 4));
        }

        AddChatSection();

        UsagePanel.Children.Add(Divider());
        if (_config.DayLimit == null && _config.MonthLimit == null)
        {
            var hint = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Theme.TextSoft };
            hint.Inlines.Add(new Run("Свои лимиты на день и месяц не заданы. "));
            var link = new Hyperlink(new Run("Задать лимиты"));
            link.Click += (_, _) => OpenSettings(DayLimitBox);
            hint.Inlines.Add(link);
            UsagePanel.Children.Add(hint);
        }
        else
        {
            if (_config.DayLimit is { } day) AddLimit("Лимит на день", snapshot.Today, day);
            if (_config.MonthLimit is { } month) AddLimit("Лимит на месяц", snapshot.MonthToDate, month);
        }

        AddFooter(snapshot.RefreshedAt, "Пополнить в кабинете", "https://tooken.club/dashboard");
    }

    private void BuildPlanPanel(ChatGptClient chat)
    {
        AddHeader("Подписка ChatGPT", refreshable: false);
        if (_chatGpt.Label is { } label) UsagePanel.Children.Add(Caption(label, bottom: 8));
        bool allowed = _chatGpt.PlanAllowed;
        UsagePanel.Children.Add(Row("Использование подписки", allowed ? "разрешено" : "не разрешено", valueBrush: allowed ? null : Theme.Warning));
        UsagePanel.Children.Add(new TextBlock
        {
            Text = chat.LimitMessage ?? "Сколько подписки уходит на Sharp Agent и какие у него лимиты, показывают настройки ChatGPT.",
            FontSize = 13, Foreground = chat.LimitMessage != null ? Theme.Warning : Theme.TextSoft,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
        });

        AddChatSection();
        AddFooter(null, "Управлять использованием", ChatGptClient.UsageUrl);
    }

    private void BuildGroqPanel(GroqLimits? limits)
    {
        AddHeader("Лимиты Groq", refreshable: false);
        if (AddPlaceholder(limits == null, "Лимиты появятся после первого ответа модели.")) return;

        AddQuota("Запросов в сутки", limits!.RequestsLeft, limits.Requests, limits.RequestsReset);
        AddQuota("Токенов в минуту", limits.TokensLeft, limits.Tokens, limits.TokensReset);
        AddChatSection();
        AddFooter(limits.Updated, "Консоль Groq", "https://console.groq.com");
    }

    private void AddQuota(string title, long? left, long? limit, DateTime? reset)
    {
        if (left is not { } remaining || limit is not > 0) return;
        UsagePanel.Children.Add(Row(title, $"осталось {Tokens.Format(remaining)} из {Tokens.Format(limit.Value)}",
            valueBrush: Low(left, limit) ? Theme.Warning : null));
        UsagePanel.Children.Add(Meter((double)remaining / limit.Value, Low(left, limit) ? Theme.Warning : Theme.Accent));
        var wait = reset - DateTime.Now;
        UsagePanel.Children.Add(Caption(wait is { } time && time > TimeSpan.Zero ? "Восстановится через " + Tokens.Duration(time) : "",
            4, bottom: 8));
    }

    private void AddChatSection()
    {
        var totals = _agent.Totals();
        var model = CurrentModel();
        long chatTokens = totals.PromptTokens + totals.CompletionTokens;
        bool tooken = _config.Provider == AgentProvider.Tooken;
        var rate = tooken ? _usage.RateOf(model) : null;
        if (chatTokens == 0 && rate == null) return;

        UsagePanel.Children.Add(Divider());
        if (chatTokens > 0)
        {
            UsagePanel.Children.Add(Row("Этот чат", Tokens.Format(chatTokens) + " токенов"));
            double charged = tooken
                ? totals.TokensByModel.Where(pair => pair.Key.Provider == AgentProvider.Tooken)
                    .Sum(pair => pair.Value * (_usage.RateOf(pair.Key.Model) ?? 0))
                : 0;
            if (charged >= 1)
                UsagePanel.Children.Add(Caption($"С баланса за него списано около {Tokens.Format((long)Math.Round(charged))}", 2));
        }
        if (ContextUsage() is { } used && _models.TryGetValue(model, out var info))
        {
            bool full = used >= ContextWarning;
            UsagePanel.Children.Add(Row("Память модели", "занято " + Tokens.Percent(used), topMargin: 8));
            UsagePanel.Children.Add(Meter(used, full ? Theme.Warning : Theme.Accent));
            UsagePanel.Children.Add(Caption(
                $"{Tokens.Format(totals.ContextTokens)} из {Tokens.Format(info.ContextTokens ?? 0)} токенов." +
                (full ? " Скоро чат перестанет помещаться — начните новый." : ""), 4));
        }
        if (rate is { } factor)
        {
            var row = Row("Коэффициент модели", "×" + factor.ToString("0.##", Ru), topMargin: 8);
            row.ToolTip = $"За один токен запроса к {model} с баланса списывается {factor.ToString("0.##", Ru)} токена.";
            UsagePanel.Children.Add(row);
        }
    }

    private void AddFooter(DateTime? updated, string? linkText, string? url)
    {
        UsagePanel.Children.Add(Divider());
        var footer = new DockPanel { LastChildFill = false };
        if (linkText != null && url != null)
        {
            var cabinet = new TextBlock { FontSize = 13 };
            var link = new Hyperlink(new Run(linkText));
            link.Click += (_, _) => FileActions.OpenUrl(url);
            cabinet.Inlines.Add(link);
            DockPanel.SetDock(cabinet, Dock.Right);
            footer.Children.Add(cabinet);
        }
        if (updated is { } time)
            footer.Children.Add(Caption(UsageLoading ? "Обновляю…"
                : _usageError != null ? "Не обновилось: " + _usageError
                : "Обновлено в " + time.ToString("HH:mm", Ru)));
        UsagePanel.Children.Add(footer);
    }

    private void AddLimit(string title, long spent, long limit)
    {
        double left = 1 - (double)spent / limit;
        bool reached = spent >= limit;
        UsagePanel.Children.Add(Row(title, reached ? "исчерпан" : "осталось " + Tokens.Percent(left),
            valueBrush: reached ? Theme.Warning : null));
        UsagePanel.Children.Add(Meter(Math.Max(0, left), Theme.Accent));
        UsagePanel.Children.Add(Caption($"Израсходовано {Tokens.Format(spent)} из {Tokens.Format(limit)}", 4, bottom: 8));
    }

    private static TextBlock Caption(string text, double top = 0, double bottom = 0) => new()
    {
        Text = text, FontSize = 12, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, top, 0, bottom),
    };

    private static Grid Row(string label, string value, double topMargin = 0, Brush? valueBrush = null)
    {
        var grid = new Grid { Margin = new Thickness(0, topMargin, 0, 6), Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = Theme.TextSoft });
        var right = new TextBlock
        {
            Text = value, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = valueBrush ?? Theme.Text,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private static ProgressBar Meter(double fraction, Brush fill) => new()
    {
        Style = Theme.Style("Meter"),
        Value = Math.Clamp(fraction, 0, 1) * 100,
        Foreground = fill,
    };

    private static Border Divider() => new() { Height = 1, Background = Theme.LineStrong, Margin = new Thickness(0, 10, 0, 10) };

    private static string Plural(int number, string one, string few, string many)
    {
        int n = Math.Abs(number) % 100, last = n % 10;
        var word = n is >= 11 and <= 14 ? many : last == 1 ? one : last is >= 2 and <= 4 ? few : many;
        return $"{number} {word}";
    }

    private string? LimitReached(SpendSnapshot snapshot)
    {
        if (_config.DayLimit is { } day && snapshot.Today >= day)
            return $"За сегодня израсходовано {Tokens.Format(snapshot.Today)} токенов при лимите {Tokens.Format(day)} в день.";
        if (_config.MonthLimit is { } month && snapshot.MonthToDate >= month)
            return $"С начала месяца израсходовано {Tokens.Format(snapshot.MonthToDate)} токенов при лимите {Tokens.Format(month)} в месяц.";
        return null;
    }

    private Task<bool> CheckLimitsAsync()
    {
        var reached = _config.Provider == AgentProvider.Tooken && _usage.Last is { } snapshot ? LimitReached(snapshot) : null;
        if (reached == null) return Task.FromResult(true);

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_state.LimitOverrideDay == today) return Task.FromResult(true);

        SetTaskbar(TaskbarItemProgressState.Paused);
        WindowEffects.Flash(this);
        bool proceed = DarkDialog.Ask(this, "Лимит расхода исчерпан",
            reached + "\n\nПродолжить работу агента? До конца дня программа больше не будет об этом спрашивать. " +
            "Изменить лимиты можно в настройках.",
            "Продолжить", "Остановить");
        SetTaskbar(TaskbarItemProgressState.Indeterminate);
        if (proceed)
        {
            _state.LimitOverrideDay = today;
            _state.Save();
        }
        else
        {
            _limitDeclined = true;
        }
        return Task.FromResult(proceed);
    }
}
