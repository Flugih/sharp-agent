using System.Collections.ObjectModel;
using System.Windows.Data;

namespace SharpAgent;

public partial class MainWindow
{
    const int ExtraPageSize = 32;
    sealed record ExtraEntry(string Text, string? Output = null, string? Log = null, bool Tool = false, bool Warning = false);
    sealed record ExtraArchive(ExtraEntry[] Entries, List<AgentRunner.PlanItem>? Plan);
    sealed record ExtraArchiveFile(string Path, long[] Offsets);

    sealed class ExtraTask(AgentRunner owner, string title, TalkTaskStatus voice)
    {
        public AgentRunner Owner { get; } = owner;
        public string Title { get; } = title;
        public TalkTaskStatus Voice { get; } = voice;
        public string Status = Tr("Думаю…", "Thinking…");
        public ObservableCollection<ExtraEntry>? Entries = [];
        public List<AgentRunner.PlanItem>? Plan;
        public readonly Dictionary<string, int> Tools = [];
        public CancellationTokenSource? Stop;
        public int? Comment;
        public ExtraArchiveFile? Archive;
    }

    sealed class ExtraView(ExtraTask task, ProgressSection progress, ListBox list, Button stop)
    {
        public ExtraTask Task { get; } = task;
        public ProgressSection Progress { get; } = progress;
        public ListBox List { get; } = list;
        public Button Stop { get; } = stop;
    }

    sealed class ExtraEntryContent(MainWindow window) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is ExtraEntry entry ? window.RenderExtraEntry(entry) : DependencyProperty.UnsetValue;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    readonly Dictionary<AgentRunner, ObservableCollection<ExtraTask>> _extraTasks = [];
    Window? _extraTaskWindow;
    ContentControl? _extraTaskPage;
    ExtraView? _extraTaskView;

    void UpdateExtraTasksButton()
    {
        int count = _extraTasks.GetValueOrDefault(_agent)?.Count ?? 0;
        ExtraTasksButton.Visibility = VisibleIf(count > 0);
        ExtraTasksButton.Content = Tr($"Доп. задачи · {count}", $"Additional tasks · {count}");
        UpdateLayoutWidths();
    }

    static ListBox ExtraList()
    {
        var item = new Style(typeof(ListBoxItem));
        item.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new(0), ItemContainerStyle = item };
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        return list;
    }

    void ExtraTasks_Click(object sender, RoutedEventArgs e)
    {
        if (_extraTaskWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        if (!_extraTasks.TryGetValue(_agent, out var tasks) || tasks.Count == 0) return;
        var list = ExtraList();
        list.Width = 180;
        list.Margin = new(8);
        list.ItemsSource = tasks;
        list.DisplayMemberPath = nameof(ExtraTask.Title);
        _extraTaskPage = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var window = _extraTaskWindow = new Window
        {
            Title = Tr("Дополнительные задачи", "Additional tasks"), Owner = ActiveChatWindow,
            Width = 720, Height = 460, MinWidth = 440, MinHeight = 260, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = Theme.Ui, FontSize = 14,
            Content = new DockPanel { Children = { list, _extraTaskPage } },
        }.Paint(BackgroundProperty, "Bg").Paint(ForegroundProperty, "Text");
        window.SourceInitialized += (_, _) => WindowEffects.ApplyFrame(window, ((SolidColorBrush)Theme.Line).Color);
        window.Closed += (_, _) =>
        {
            _extraTaskPage!.Content = null;
            _extraTaskWindow = null;
            _extraTaskPage = null;
            _extraTaskView = null;
        };
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is ExtraTask task && _extraTaskPage != null) await ShowExtraTaskAsync(task);
        };
        list.SelectedIndex = tasks.Count - 1;
        window.Show();
    }

    async Task ShowExtraTaskAsync(ExtraTask task)
    {
        var progress = new ProgressSection(task.Status);
        var list = ExtraList();
        list.Margin = new(0, 8, 0, 0);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding { Converter = new ExtraEntryContent(this) });
        list.ItemTemplate = new DataTemplate { VisualTree = presenter };
        var stop = new Button { Style = Theme.Style("GhostButton"), Content = Tr("Остановить", "Stop"), HorizontalAlignment = HorizontalAlignment.Right };
        stop.Click += (_, _) => task.Stop?.Cancel();
        var header = new StackPanel
        {
            Children = { new TextBlock { Text = task.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, stop, progress.View },
        };
        DockPanel.SetDock(header, Dock.Top);
        var view = _extraTaskView = new ExtraView(task, progress, list, stop);
        _extraTaskPage!.Content = new DockPanel { Margin = new(16, 8, 16, 16), Children = { header, list } };
        if (task.Stop != null) progress.Start();
        RefreshExtraStatus(task);
        if (task.Entries is { } live)
        {
            list.ItemsSource = live;
            return;
        }
        var file = task.Archive!;
        var entries = new ObservableCollection<ExtraEntry>();
        int page = 0;
        bool loading = false;
        list.ItemsSource = entries;
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(async (_, e) =>
        {
            if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 40) await LoadPageAsync();
        }));
        await LoadPageAsync();

        async Task LoadPageAsync()
        {
            if (loading || page + 1 >= file.Offsets.Length || _extraTaskView != view) return;
            loading = true;
            try
            {
                using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
                source.Position = file.Offsets[page];
                var bytes = new byte[checked((int)(file.Offsets[page + 1] - source.Position))];
                await source.ReadExactlyAsync(bytes);
                var archive = System.Text.Json.JsonSerializer.Deserialize<ExtraArchive>(bytes);
                if (_extraTaskView != view || archive == null) return;
                page++;
                if (archive.Plan is { } plan) progress.SetPlan(plan, animate: false);
                foreach (var entry in archive.Entries) entries.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                page = file.Offsets.Length;
                if (_extraTaskView == view) entries.Add(new(Tr("Подробности задачи больше недоступны.", "Task details are no longer available.")));
            }
            finally { loading = false; }
        }
    }

    void RefreshExtraStatus(ExtraTask task)
    {
        if (_extraTaskView is not { } view || view.Task != task) return;
        view.Stop.Visibility = VisibleIf(task.Stop != null);
        if (task.Stop == null) view.Progress.Finish(task.Status);
        else view.Progress.SetTitle(task.Status);
        view.Progress.View.IsExpanded = true;
        if (task.Plan is { } plan) view.Progress.SetPlan(plan, animate: task.Stop != null);
    }

    void SetExtraPlan(ExtraTask task, List<AgentRunner.PlanItem> plan)
    {
        task.Plan = plan.Select(item => item with { Step = VoiceDetail(item.Step, 400) ?? Tr("Шаг плана", "Plan step") }).ToList();
        RefreshExtraStatus(task);
    }

    async Task RunExtraTaskAsync(TalkInput input, string model)
    {
        var agent = CreateAgent(temporary: true);
        agent.ClarifyAsync = null;
        var voice = new TalkTaskStatus();
        var task = new ExtraTask(_agent, VoiceDetail(input.Text, 110) ?? Tr("Дополнительная задача", "Additional task"), voice);
        var run = new ChatRun(agent, () => { }) { Extra = task, Voice = voice };
        task.Stop = run.Cancellation;
        _runs.Add(agent, run);
        if (!_extraTasks.TryGetValue(task.Owner, out var tasks)) _extraTasks[task.Owner] = tasks = [];
        tasks.Add(task);
        UpdateExtraTasksButton();
        var entries = task.Entries!;
        void Status(string text)
        {
            task.Status = text;
            RefreshExtraStatus(task);
        }
        agent.ThinkingStarted += () =>
        {
            task.Comment = null;
            Status(Tr("Думаю…", "Thinking…"));
        };
        agent.ProgressReported += progress =>
        {
            if (VoiceDetail(progress.Commentary, 1_200) is not { } comment) return;
            if (task.Comment is { } at)
            {
                if (entries[at].Text != comment) entries[at] = new(comment);
            }
            else
            {
                task.Comment = entries.Count;
                entries.Add(new(comment));
            }
        };
        agent.Retrying += (attempt, total) => Status(Tr($"Переподключение {attempt}/{total}", $"Reconnecting {attempt}/{total}"));
        agent.ToolCallStarted += call =>
        {
            if (call.Name != "update_plan") Status(Tr("Выполняю инструмент…", "Running a tool…"));
            AddExtraTool(task, entries, call);
        };
        agent.ToolCallFinished += result => FinishExtraTool(task, entries, result);
        agent.AssistantText += (text, _, _) =>
        {
            if (VoiceDetail(text, 12_000) is { } answer) entries.Add(new(answer));
        };
        agent.Warning += text => entries.Add(new(VoiceDetail(text, 800) ?? Tr("Предупреждение", "Warning"), Warning: true));
        using (var access = _toolContext.FileAccess.Begin(allowConcurrentReads: true))
            try
            {
                await agent.RunAsync(input.Text, input.Attachments, model, run.Cancellation.Token, useSelectedModel: true, replyLanguage: input.Language);
                if (voice is { Failure: null, Report: null })
                    voice.Failure = Tr("Агент не вернул итоговый ответ.", "The agent did not return a final answer.");
            }
            catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested || run.LimitDeclined)
            {
                voice.Failure = Tr("Задача остановлена.", "Task stopped.");
            }
            catch (Exception ex)
            {
                voice.Failure = VoiceDetail(ex.Message, 350) ?? Tr("Ошибка выполнения.", "The task encountered an error.");
            }
            finally
            {
                if (voice.Failure is { } error) entries.Add(new(error, Warning: true));
                foreach (int at in task.Tools.Values) entries[at] = entries[at] with { Output = Tr("Выполнение прервано.", "Interrupted"), Warning = true };
                task.Tools.Clear();
                task.Stop = null;
                Status(voice.Failure == null ? Tr("Выполнено", "Done") : Tr("Выполнение прервано", "Interrupted"));
                FinishTalkTask(agent, voice, run.Cancellation.IsCancellationRequested);
                run.Cancellation.Dispose();
                _runs.Remove(agent);
                if (!AnyRunning && _provider is ChatGptClient && !_chatGpt.PlanAllowed) ApplyProvider();
            }
        _ = ArchiveExtraTaskAsync(task, new([.. entries], task.Plan));
    }

    static async Task ArchiveExtraTaskAsync(ExtraTask task, ExtraArchive archive)
    {
        var path = Path.Combine(AppPaths.CacheFolder, $"extra-{Guid.NewGuid():N}.json");
        try
        {
            var offsets = await Task.Run(async () =>
            {
                Directory.CreateDirectory(AppPaths.CacheFolder);
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                List<long> pages = [];
                for (int at = 0; at < Math.Max(1, archive.Entries.Length); at += ExtraPageSize)
                {
                    pages.Add(file.Position);
                    var entries = archive.Entries.AsSpan(at, Math.Min(ExtraPageSize, archive.Entries.Length - at)).ToArray();
                    await System.Text.Json.JsonSerializer.SerializeAsync(file, new ExtraArchive(entries, at == 0 ? archive.Plan : null));
                }
                pages.Add(file.Position);
                return pages.ToArray();
            });
            task.Archive = new(path, offsets);
            task.Entries = null;
            task.Plan = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    void AddExtraTool(ExtraTask task, ObservableCollection<ExtraEntry> entries, ToolCallEvent call)
    {
        if (call.Name == "update_plan") return;
        if (call.Name == "exec_command" && ArgumentOf(call.ArgumentsJson, "session_id") is { } session && task.Tools.Remove(session, out int pending))
        {
            task.Tools[call.Id] = pending;
            return;
        }
        task.Tools[call.Id] = entries.Count;
        entries.Add(new(VoiceDetail(CommandText(call.ArgumentsJson), 260) ?? Describe(call.Name).Label, Tool: true));
    }

    void FinishExtraTool(ExtraTask task, ObservableCollection<ExtraEntry> entries, ToolResultEvent result)
    {
        if (!task.Tools.Remove(result.Id, out int at)) return;
        if (result.Name == "exec_command" && ShellTools.Sessions.RunningId(result.Output) is { } session)
        {
            task.Tools[session] = at;
            return;
        }
        var title = entries[at].Text + (result.IsError ? Tr(" · ошибка", " · error")
            : result.IsWarning ? Tr(" · предупреждение", " · warning") : Tr(" · выполнено", " · done"));
        entries[at] = new(title, VoiceDetail(result.Output, 3_000) ?? Tr("Нет вывода.", "No output."),
            result.Name is "exec_command" or "run_plugin" ? CommandLog.IdOf(result.Output) : null, true, result.IsError || result.IsWarning);
    }

    FrameworkElement RenderExtraEntry(ExtraEntry entry)
    {
        string color = entry.Warning ? "Warning" : entry.Tool ? "TextMuted" : "Text";
        if (!entry.Tool) return ChatText(entry.Text, color, Tr("Подробности задачи", "Task details"));
        var label = new Shimmer { Text = entry.Text, TextWrapping = TextWrapping.Wrap, IsActive = entry.Output == null, Base = color };
        if (entry.Output == null) return label;
        var row = new Expander { Style = Theme.Style("ProgressSection"), Header = label };
        row.Expanded += (_, _) =>
        {
            if (row.Content != null) return;
            var details = new StackPanel { Children = { ChatText(entry.Output, "TextSoft", Tr("Результат инструмента", "Tool result")) } };
            if (entry.Log != null)
            {
                var log = new Button { Content = Tr("Открыть журнал", "Open log"), Style = Theme.Style("GhostButton") };
                log.Click += (_, _) =>
                {
                    if (CommandLog.PathOf(AppPaths.LogsFolder, entry.Log) is { } path && File.Exists(path))
                    {
                        ActivateChatWindow();
                        ShowPreview(path, title: entry.Text);
                    }
                    else ShowToast(Tr("Журнал команды удалён.", "The command log was deleted."), ToastKind.Warning);
                };
                details.Children.Add(log);
            }
            row.Content = details;
        };
        return row;
    }
}
