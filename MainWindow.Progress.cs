using System.Windows.Documents;
using System.Windows.Shell;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    sealed class ProgressSection
    {
        readonly Shimmer _label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        readonly DockPanel _header = new() { MinHeight = 25 };
        TextBlock? _error, _reconnectGlyph;
        int _errors;
        bool _expandAfterReconnect;
        public readonly StackPanel Items = new();
        public readonly Expander View = new() { Style = Theme.Style("ProgressSection") };
        public PlanCard? Plan;
        public ProgressSection? Searches;
        public bool IsRunning => _label.IsActive;
        public Shimmer Label => _label;
        public string? ReconnectStatus { get; private set; }

        public ProgressSection(string title, string? glyph = null)
        {
            if (glyph != null) _header.Children.Add(GlyphText(glyph, 16, "TextMuted", new(0, 0, 8, 0)));
            _header.Children.Add(_label);
            View.Header = _header;
            // Children sit 34 in, with a guide line under the centre of the group glyph.
            View.Content = glyph == null ? Items : new Border
            {
                BorderThickness = new(1, 0, 0, 0), Margin = new(7, 0, 0, 0), Padding = new(26, 0, 0, 0), Child = Items,
            }.Paint(BorderBrushProperty, "Line");
            SetTitle(title);
        }

        public void SetTitle(string title, string? label = null, bool animate = true)
        {
            _label.SetAnimatedText(label ?? title, animate);
            AutomationProperties.SetName(View, title);
        }

        public void SetReconnectStatus(string? status)
        {
            if (ReconnectStatus == status) return;
            if (ReconnectStatus == null)
            {
                _expandAfterReconnect = View.IsExpanded && Items.Children.Count == 0;
                if (_expandAfterReconnect) View.IsExpanded = false;
                _reconnectGlyph ??= GlyphText(Glyphs.Wifi, 14, "TextMuted", new(0, 0, 8, 0));
                _header.Children.Insert(0, _reconnectGlyph);
                _label.Base = "Text";
            }
            else if (status == null)
            {
                _header.Children.Remove(_reconnectGlyph!);
                _label.Base = "TextMuted";
                if (_expandAfterReconnect && IsRunning) View.IsExpanded = true;
            }
            ReconnectStatus = status;
        }

        public void ShowError(string text)
        {
            if (_error == null)
            {
                _error = new TextBlock { Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.Paint(ForegroundProperty, "DangerText");
                _header.Children.Add(_error);
            }
            _error.Text = ++_errors > 1 ? Tr($"Ошибок: {_errors} · {text}", $"Errors: {_errors} · {text}") : text;
        }

        public static ProgressSection Collapsed(string title, string glyph, Panel parent)
        {
            var group = new ProgressSection(title, glyph);
            parent.Children.Add(group.View);
            return group;
        }

        public static void AddSearch(ref ProgressSection? searches, Panel parent, UIElement item)
        {
            searches ??= Collapsed(Tr("Поиск текста", "Text search"), Glyphs.Search, parent);
            searches.Items.Children.Add(item);
            searches.SetTitle(Tr($"Поиск текста · {searches.Items.Children.Count}", $"Text search · {searches.Items.Children.Count}"));
        }

        public void Start()
        {
            _label.IsActive = View.IsExpanded = true;
            Plan?.Update(IsRunning);
        }

        public void Finish(string title)
        {
            SetTitle(title);
            _label.IsActive = false;
            SetReconnectStatus(null);
            View.IsExpanded = false;
            Plan?.Update(IsRunning);
        }
    }

    sealed class PlanCard
    {
        readonly ProgressSection _section = new("", Glyphs.Check) { View = { IsExpanded = true } };
        readonly ProgressBar _meter = new() { Style = Theme.Style("Meter"), Margin = new(0, 8, 0, 6) };
        readonly StackPanel _tasks = new();
        List<AgentRunner.PlanItem> _items = [];
        bool _showingSpinner;
        public FrameworkElement View => _section.View;

        public PlanCard()
        {
            _section.Items.Children.Add(_meter);
            _section.Items.Children.Add(_tasks);
        }

        public void Update(bool running, List<AgentRunner.PlanItem>? items = null, bool animate = false)
        {
            if (items == null && !running && !_showingSpinner) return;
            _items = items ?? _items;
            items = _items;
            int done = items.Count(item => item.Done);
            int current = items.FindIndex(item => item.Status == "in_progress");
            if (current < 0) current = items.FindIndex(item => !item.Done);
            var step = current >= 0 ? items[current].Step : Tr("План выполнен", "Plan complete");
            var summary = Tr($"План. Выполнено {done} из {items.Count}", $"Plan. {done} of {items.Count} completed");
            _section.SetTitle(summary, $"{done}/{items.Count} · {step}", animate);
            AutomationProperties.SetName(_meter, summary);
            _meter.Maximum = items.Count;
            _meter.Value = done;
            _showingSpinner = running && current >= 0;
            _tasks.Children.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                bool active = _showingSpinner && i == current;
                FrameworkElement mark = active ? MakeSpinner(16) : new CheckBox
                {
                    Style = Theme.Style("PlanCheck"), IsChecked = item.Done,
                };
                mark.VerticalAlignment = VerticalAlignment.Top;
                mark.Margin = new(0, 2, 8, 0);
                AutomationProperties.SetName(mark, item.Step);
                var text = new TextBlock
                {
                    Text = item.Step, TextWrapping = TextWrapping.Wrap, TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, active ? "Text" : "TextMuted");
                if (active) AutomationProperties.SetItemStatus(text, Tr("В работе", "In progress"));
                _tasks.Children.Add(new DockPanel { Margin = new(0, 6, 0, 6), Children = { mark, text } });
            }
        }
    }

    const int ToolStatusGraceMs = 900, WorkStatusDelayMs = 400;
    readonly StringBuilder _draftText = new();
    DispatcherTimer? _draftTimer;
    DispatcherTimer? _modelProgressTimer;
    DispatcherTimer? _workStatusTimer;
    (string Text, long Since)? _pendingWorkStatus;
    ModelProgress? _pendingModelProgress;
    RichTextBox? _draftView;
    int _draftShown;
    bool _draftIsProgress, _preparingAnswer;

    sealed record ComposerLoad(string Label, string? Remaining = null, double Percent = 0, bool IsIndeterminate = true, bool IsIndexing = false);
    int _loadingRefreshQueued;
    bool ChatSubmitBusy => ChatFileBusy || _toolContext.ProjectWatcher.Progress != null;

    void OnIndexProgressChanged()
    {
        if (Interlocked.Exchange(ref _loadingRefreshQueued, 1) != 0) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _loadingRefreshQueued, 0);
            UpdateSendButton();
        }, DispatcherPriority.Background);
    }

    string? UpdateComposerLoading()
    {
        ComposerLoad? load = null;
        if (_toolContext.ProjectWatcher.Progress is { } progress)
        {
            var label = progress.Phase switch
            {
                ProjectWatcher.IndexPhase.Documents => Tr("Индексация документов", "Indexing documents"),
                ProjectWatcher.IndexPhase.Scans => Tr("Распознавание сканов", "Recognizing scanned documents"),
                _ => Tr("Индексация проекта", "Indexing project"),
            };
            load = progress.Total == 0 ? new(label, IsIndexing: true) : new(label,
                progress.Done < progress.Total ? Tr($"Осталось {progress.Total - progress.Done} из {progress.Total}", $"{progress.Total - progress.Done} of {progress.Total} remaining")
                    : Tr("Завершение…", "Finishing…"),
                100d * progress.Done / progress.Total, IsIndeterminate: false, IsIndexing: true);
        }
        else if (_attachmentImports > 0) load = new(Tr("Подготовка вложений…", "Preparing attachments…"));
        else if (_chatOperation != null) load = new(Tr("Загрузка данных чата…", "Loading chat data…"));
        var status = IsShown(ChatSkeleton) && load is { IsIndexing: false } ? null : load;
        if (!Equals(ComposerLoadingStatus.Content, status)) ComposerLoadingStatus.Content = status;
        return load?.Label;
    }

    ProgressSection EnsureProgress()
    {
        if (_chatView.Progress is { } progress) return progress;
        progress = _chatView.Progress = new(Tr("Ход работы", "Progress"));
        progress.View.Margin = new(0, 8, 0, 8);
        AppendToChat(progress.View);
        return progress;
    }

    void StartProgress()
    {
        ResetProgressStep();
        _chatView.ActiveTools.Clear();
        _chatView.FinishedToolStatus = null;
        _chatView.ContextStatus = null;
        _chatView.Compacting = false;
        _pendingWorkStatus = null;
        EnsureProgress().Start();
        SetProgressHint(Tr("Подготовка запроса…", "Preparing request…"));
        UpdateWorkStatus();
        _lastErrorCard = null;
        _compactChat?.ShowError(null);
        _compactChat?.ShowDraft(null);
    }

    void SetProgressHint(string? text)
    {
        if (text != null && _cts != null && !_suppressAnimation) _compactChat?.SetStatus(text);
        if (_chatView.ProgressHint != null)
        {
            _chatView.Progress?.Items.Children.Remove(_chatView.ProgressHint);
            _chatView.ProgressHint = null;
        }
        if (text == null) return;
        _chatView.ProgressHint = new TextBlock { Text = text, Margin = new(0, 6, 0, 6) }.Paint(ForegroundProperty, "TextMuted");
        EnsureProgress().Items.Children.Add(_chatView.ProgressHint);
    }

    void FinishProgress(string title)
    {
        FlushModelProgress();
        if (_chatView.Progress is not { } progress) return;
        ReleaseStickyProgress();
        SetProgressHint(null);
        if (progress.Items.Children.Count == 0) SetProgressHint(Tr("Нет подробностей по этому запросу", "No details for this request"));
        progress.Finish(title);
        _chatView.ActiveTools.Clear();
        _chatView.FinishedToolStatus = null;
        _chatView.ContextStatus = null;
        _workStatusTimer?.Stop();
        _chatView.Compacting = false;
        ResetProgressStep();
    }

    void ResetProgressStep()
    {
        FlushModelProgress();
        _chatView.Commands = null;
        _chatView.ReasoningRow = _chatView.CommentaryRow = null;
    }

    void UpdateWorkStatus()
    {
        _workStatusTimer?.Stop();
        if (_chatView.Progress is not { IsRunning: true } progress) return;
        long now = Environment.TickCount64, delay = 0;
        bool reconnecting = progress.ReconnectStatus != null;
        bool confirming = CurrentRun?.Question != null || Taskbar.ProgressState == TaskbarItemProgressState.Paused;
        var status = progress.ReconnectStatus ?? (confirming ? Tr("Ожидаю подтверждения…", "Waiting for confirmation…") : null)
            ?? _chatView.ActiveTools.Values.FirstOrDefault();
        if (status == null && _chatView.FinishedToolStatus is { } finished && finished.Until > now)
        {
            status = finished.Status;
            delay = finished.Until - now;
        }
        else if (_chatView.FinishedToolStatus is { } previous && previous.Until <= now) _chatView.FinishedToolStatus = null;
        status ??= _chatView.Compacting ? Tr("Сжатие контекста…", "Compacting context…") : _chatView.ContextStatus ??
            (_preparingAnswer ? Tr("Рассуждаю…", "Reasoning…") : Tr("Думаю…", "Thinking…"));
        if (!_suppressAnimation && !reconnecting && !confirming && progress.Label.Text != status)
        {
            if (_pendingWorkStatus?.Text != status) _pendingWorkStatus = (status, now);
            long remaining = WorkStatusDelayMs - (now - _pendingWorkStatus.Value.Since);
            if (remaining > 0)
            {
                delay = delay > 0 ? Math.Min(delay, remaining) : remaining;
                status = progress.Label.Text;
            }
        }
        else _pendingWorkStatus = null;
        if (delay > 0 && !_suppressAnimation)
        {
            var interval = TimeSpan.FromMilliseconds(delay);
            _workStatusTimer ??= new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => UpdateWorkStatus(), Dispatcher);
            _workStatusTimer.Interval = interval;
            _workStatusTimer.Start();
        }
        if (progress.Label.Text != status) progress.SetTitle(status, animate: true);
        _compactChat?.SetStatus(status, reconnecting ? "Text" : "TextMuted");
    }

    void ShowReconnect(int attempt, int total)
    {
        SetProgressHint(null);
        EnsureProgress().SetReconnectStatus(Tr($"Переподключение {attempt}/{total}", $"Reconnecting {attempt}/{total}"));
        UpdateWorkStatus();
    }

    void ClearReconnect()
    {
        if (_chatView.Progress is not { ReconnectStatus: not null } progress) return;
        progress.SetReconnectStatus(null);
        UpdateWorkStatus();
    }

    void ShowCompacting()
    {
        _chatView.Compacting = true;
        _chatView.Progress?.SetReconnectStatus(null);
        SetProgressHint(null);
        UpdateWorkStatus();
    }

    void ShowPreparingContext(string status)
    {
        _chatView.ContextStatus = status;
        _chatView.Compacting = false;
        UpdateWorkStatus();
    }

    void ShowThinking()
    {
        ResetProgressStep();
        _chatView.Compacting = false;
        _chatView.ContextStatus = null;
        _chatView.Progress?.SetReconnectStatus(null);
        SetProgressHint(null);
        UpdateWorkStatus();
    }

    void OnModelProgress(ModelProgress progress)
    {
        if (progress is { Reasoning.Length: > 0 } or { Commentary.Length: > 0 }) ClearReconnect();
        _pendingModelProgress = progress;
        if (_suppressAnimation)
        {
            FlushModelProgress();
            return;
        }
        _modelProgressTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => FlushModelProgress(), Dispatcher);
        _modelProgressTimer.Start();
    }

    void FlushModelProgress()
    {
        _modelProgressTimer?.Stop();
        if (_pendingModelProgress is not { } progress) return;
        _pendingModelProgress = null;
        UpdateProgressText(ref _chatView.ReasoningRow, progress.Reasoning, Tr("Рассуждения модели", "Model reasoning"));
        UpdateProgressText(ref _chatView.CommentaryRow, progress.Commentary, Tr("Промежуточное сообщение", "Interim message"));
    }

    void UpdateProgressText(ref RichTextBox? row, string? markdown, string label)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            if (row != null) _chatView.Progress?.Items.Children.Remove(row);
            row = null;
        }
        else if (row == null) AddProgressItem(row = ProgressText(markdown, label));
        else if (!row.Selection.IsEmpty || row.IsMouseCaptureWithin) row.Tag = ChatDocument(markdown, "TextSoft");
        else
        {
            row.Tag = null;
            row.Document = ChatDocument(markdown, "TextSoft");
        }
    }

    void OnAssistantDelta(string? text, bool usesTools)
    {
        if (text == null)
        {
            ClearDraft();
            UpdateWorkStatus();
            return;
        }
        if (text.Length > 0 || usesTools) ClearReconnect();
        _draftText.Append(text);
        _draftIsProgress |= usesTools;
        bool preparing = !_draftIsProgress && (_preparingAnswer || !string.IsNullOrWhiteSpace(text));
        if (_preparingAnswer != preparing)
        {
            _preparingAnswer = preparing;
            UpdateWorkStatus();
        }
        _draftTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            _draftTimer?.Stop();
            RenderDraft();
        }, Dispatcher);
        _draftTimer.Start();
    }

    void RenderDraft()
    {
        var text = _draftText.ToString();
        _compactChat?.ShowDraft(_draftIsProgress ? null : text);
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_draftView == null)
        {
            var document = new FlowDocument(new Paragraph(new Run()) { Margin = new(0) })
            {
                PagePadding = new(0), FontFamily = Theme.Ui, FontSize = 14, LineHeight = 23,
            }.Paint(TextElement.ForegroundProperty, _draftIsProgress ? "TextSoft" : "Text");
            _draftView = ReadOnlyChatText(document, Tr("Ответ агента", "Agent response"));
            _draftView.Margin = new(-5, 8, -5, 8);
            _draftView.ContextMenu = (ContextMenu)Application.Current.Resources["ReadOnlyTextMenu"];
            _draftShown = 0;
            if (!_draftIsProgress) AppendToChat(_draftView);
        }
        if (_draftIsProgress && _draftView.Parent != EnsureProgress().Items)
        {
            ChatPanel.Children.Remove(_draftView);
            _draftView.Document.Paint(TextElement.ForegroundProperty, "TextSoft");
            foreach (var run in ((Paragraph)_draftView.Document.Blocks.FirstBlock).Inlines.OfType<Run>())
                if (run.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush brush)
                    brush.Color = ((SolidColorBrush)Application.Current.FindResource("TextSoft")).Color;
            AutomationProperties.SetName(_draftView, Tr("Промежуточное сообщение", "Interim message"));
            AddProgressItem(_draftView);
        }
        AppendDraft(text[_draftShown..]);
        _draftShown = text.Length;
    }

    // Merge completed chunks only while selection is idle, preserving text pointers during a drag.
    void AppendDraft(string chunk)
    {
        if (chunk.Length == 0) return;
        var paragraph = (Paragraph)_draftView!.Document.Blocks.FirstBlock;
        var settled = (Run)paragraph.Inlines.LastInline;
        if (!Theme.AnimationsEnabled)
        {
            settled.ContentEnd.InsertTextInRun(chunk);
            return;
        }
        var brush = new SolidColorBrush(((SolidColorBrush)Application.Current.FindResource(_draftIsProgress ? "TextSoft" : "Text")).Color);
        var run = new Run(chunk) { Foreground = brush };
        paragraph.Inlines.Add(run);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            if (_draftView?.Document != paragraph.Parent || !_draftView.Selection.IsEmpty || _draftView.IsMouseCaptureWithin || run.PreviousInline is not Run previous) return;
            previous.ContentEnd.InsertTextInRun(run.Text);
            paragraph.Inlines.Remove(run);
        };
        brush.BeginAnimation(Brush.OpacityProperty, fade);
    }

    void ClearDraft(bool interrupted = false)
    {
        _draftTimer?.Stop();
        var partial = interrupted && !_draftIsProgress ? _draftText.ToString() : null;
        if (interrupted && _draftText.Length > 0)
        {
            _draftIsProgress = true;
            RenderDraft();
            if (_draftView != null) AutomationProperties.SetName(_draftView, Tr("Незавершённый ответ", "Incomplete response"));
        }
        else if (_draftView?.Parent is Panel parent) parent.Children.Remove(_draftView);
        _draftText.Clear();
        _draftView = null;
        _draftIsProgress = false;
        _preparingAnswer = false;
        _compactChat?.ShowDraft(partial);
    }

    void OnAssistantText(string markdown, TimeSpan? elapsed, DateTimeOffset sentAt, string[] nextActions)
    {
        ClearReconnect();
        FlushModelProgress();
        ClearDraft();
        if (elapsed != null)
        {
            ShowFinalAnswer(markdown, elapsed, sentAt, nextActions);
            SyncCompactChat();
            _compactChat?.SetStatus(string.IsNullOrWhiteSpace(markdown) ? Tr("Запрос завершён", "Request complete") : Tr("Ответ готов", "Response ready"), "Success");
        }
        else if (!string.IsNullOrWhiteSpace(markdown)) AddProgressItem(ProgressText(markdown, Tr("Промежуточное сообщение", "Interim message")));
    }

    void ShowFinalAnswer(string markdown, TimeSpan? elapsed, DateTimeOffset? sentAt, string[] nextActions)
    {
        _chatView.NextActions = nextActions;
        EnsureProgress();
        FinishProgress(elapsed is { } time ? Tr("Выполнено за ", "Completed in ") + Tokens.Duration(time) : Tr("Выполнено", "Completed"));
        _chatView.Progress = null;
        _chatView.ProgressHint = null;
        if (!string.IsNullOrWhiteSpace(markdown)) AddAssistantMessage(markdown, sentAt);
    }

    RichTextBox ProgressText(string markdown, string label)
    {
        var text = ChatText(markdown, "TextSoft", label);
        text.Margin = text.Margin with { Top = 6, Bottom = 6 };
        return text;
    }

    void AddProgressItem(UIElement item)
    {
        SetProgressHint(null);
        EnsureProgress().Items.Children.Add(item);
    }

    void CompletePlan(string id, string argumentsJson, string output, bool isError)
    {
        if (isError)
        {
            CompleteToolRow(AddToolRow(id, "update_plan", argumentsJson), output, true);
            return;
        }
        var items = AgentRunner.PlanItems(ParseObject(argumentsJson));
        if (items.Count == 0) return;
        SetProgressHint(null);
        var progress = EnsureProgress();
        if (progress.Plan == null)
        {
            progress.Plan = new();
            progress.Items.Children.Insert(0, progress.Plan.View);
        }
        progress.Plan.Update(progress.IsRunning, items, animate: progress.IsRunning && !_suppressAnimation);
    }

    void AddProgressTool(string name, UIElement item)
    {
        if (name == "search_text")
        {
            var progress = EnsureProgress();
            SetProgressHint(null);
            if (progress.Searches == null) _chatView.Commands = null;
            ProgressSection.AddSearch(ref progress.Searches, progress.Items, item);
            return;
        }
        if (name is not ("run_command" or "run_plugin"))
        {
            _chatView.Commands = null;
            AddProgressItem(item);
            return;
        }
        if (_chatView.Commands == null)
        {
            var commands = _chatView.Commands = new(Tr("Команды", "Commands"), Glyphs.Terminal) { View = { IsExpanded = true } };
            AddProgressItem(commands.View);
        }
        _chatView.Commands.Items.Children.Add(item);
    }
}
