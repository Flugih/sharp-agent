namespace SharpAgent;

public partial class MainWindow
{
    sealed class ProgressSection
    {
        readonly TextBlock _label = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center }.Paint(ForegroundProperty, "TextMuted");
        readonly StackPanel _header = new() { Orientation = Orientation.Horizontal };
        FrameworkElement? _spinner;
        public readonly StackPanel Items = new();
        public readonly Expander View = new() { Style = Theme.Style("ProgressSection") };
        public PlanCard? Plan;
        public ProgressSection? Searches;

        public ProgressSection(string title, string? glyph = null)
        {
            if (glyph != null) _header.Children.Add(GlyphText(glyph, 13, "TextMuted", new(0, 0, 8, 0)));
            _header.Children.Add(_label);
            View.Header = _header;
            View.Content = glyph == null ? Items : new Border
            {
                BorderThickness = new(1, 0, 0, 0), Margin = new(11, 2, 0, 3), Padding = new(13, 0, 0, 0), Child = Items,
            }.Paint(BorderBrushProperty, "Line");
            SetTitle(title);
        }

        public void SetTitle(string title)
        {
            _label.Text = title;
            AutomationProperties.SetName(View, title);
            View.ToolTip = title + " · раскрыть или свернуть";
        }

        public static void AddSearch(ref ProgressSection? searches, Panel parent, UIElement item)
        {
            if (searches == null)
            {
                searches = new("Поиск текста", Glyphs.Search);
                searches.View.IsExpanded = false;
                searches.View.Margin = new(-3, 2, 0, 2);
                parent.Children.Add(searches.View);
            }
            searches.Items.Children.Add(item);
            searches.SetTitle($"Поиск текста · {searches.Items.Children.Count}");
        }

        public void Start()
        {
            SetTitle("Думаю…");
            View.IsExpanded = true;
            _spinner = MakeSpinner(12);
            _spinner.Margin = new(0, 0, 8, 0);
            _header.Children.Insert(0, _spinner);
        }

        public void Finish(string title)
        {
            SetTitle(title);
            View.IsExpanded = false;
            if (_spinner == null) return;
            StopSpinner(_spinner);
            _header.Children.Remove(_spinner);
            _spinner = null;
        }
    }

    sealed class PlanCard
    {
        const double TextInset = 42;
        readonly TextBlock _count = new() { FontSize = 12, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _title = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        readonly ProgressBar _meter = new() { Style = Theme.Style("Meter"), Margin = new(0, 8, 0, 0) };
        readonly StackPanel _tasks = new() { Margin = new(0, 4, 0, 1) };
        readonly Expander _expander = new()
        {
            Style = Theme.Style("ProgressSection"), HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = true,
        };
        public readonly Border View;

        public PlanCard()
        {
            _count.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            _title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var countBadge = new Border
            {
                CornerRadius = new(6), Padding = new(0, 2, 0, 2), Width = 32,
                Margin = new(0, 0, TextInset - 32, 0), Child = _count,
            };
            _count.TextAlignment = TextAlignment.Center;
            countBadge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            DockPanel.SetDock(countBadge, Dock.Left);
            _expander.Header = new DockPanel { Children = { countBadge, _title } };
            _expander.Content = new StackPanel { Margin = new(3, 0, 3, 0), Children = { _meter, _tasks } };
            View = new Border
            {
                BorderThickness = new(1), CornerRadius = new(10),
                Margin = new(0, 4, 0, 8), Padding = new(8, 6, 8, 6), Child = _expander,
            };
            View.SetResourceReference(Border.BackgroundProperty, "Surface");
            View.SetResourceReference(Border.BorderBrushProperty, "Line");
        }

        public void Update(IReadOnlyList<AgentRunner.PlanItem> items)
        {
            int done = items.Count(item => item.Done);
            _count.Text = $"{done}/{items.Count}";
            _title.Text = items.FirstOrDefault(item => item.Status == "in_progress").Step ??
                          items.FirstOrDefault(item => !item.Done).Step ?? "План выполнен";
            _title.ToolTip = _title.Text;
            var summary = $"План. Выполнено {done} из {items.Count}";
            AutomationProperties.SetName(_expander, summary);
            AutomationProperties.SetName(_meter, summary);
            _meter.Maximum = items.Count;
            _meter.Value = done;
            _tasks.Children.Clear();
            foreach (var item in items)
            {
                bool active = item.Status == "in_progress";
                var check = new CheckBox
                {
                    Style = Theme.Style("PlanCheck"), IsThreeState = true, IsChecked = active ? null : item.Done,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 1, 8, 0),
                };
                AutomationProperties.SetName(check, item.Step);
                DockPanel.SetDock(check, Dock.Left);
                var text = new TextBlock
                {
                    Text = item.Step, FontSize = 13,
                    TextWrapping = TextWrapping.Wrap, TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, active ? "Text" : "TextMuted");
                var row = new DockPanel { Margin = new(0, 3, 0, 3), Children = { check, text } };
                if (active)
                {
                    var status = new TextBlock
                    {
                        Text = "В работе", FontSize = 12, Margin = new(10, 1, 0, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    status.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                    DockPanel.SetDock(status, Dock.Right);
                    row.Children.Insert(1, status);
                }
                _tasks.Children.Add(row);
            }
        }
    }

    readonly StringBuilder _draftText = new();
    DispatcherTimer? _draftTimer;
    TextBox? _draftView;
    bool _draftIsProgress;

    ProgressSection EnsureProgress()
    {
        if (_chatView.Progress is { } progress) return progress;
        progress = _chatView.Progress = new("Ход работы");
        progress.View.Margin = new(-3, 8, 0, 0);
        progress.Items.Margin = new(3, 2, 0, 2);
        AppendToChat(progress.View);
        return progress;
    }

    void StartProgress()
    {
        ResetProgressStep();
        EnsureProgress().Start();
        SetProgressHint("Подготовка запроса…");
        _compactChat?.ShowError(null);
    }

    void SetProgressHint(string? text)
    {
        if (text != null) _compactChat?.SetStatus(text);
        if (_chatView.ProgressHint != null)
        {
            _chatView.Progress?.Items.Children.Remove(_chatView.ProgressHint);
            _chatView.ProgressHint = null;
        }
        if (text == null) return;
        _chatView.ProgressHint = new TextBlock { Text = text, FontSize = 13, Margin = new(0, 5, 0, 5) }.Paint(ForegroundProperty, "TextMuted");
        EnsureProgress().Items.Children.Add(_chatView.ProgressHint);
    }

    void FinishProgress(string title)
    {
        if (_chatView.Progress is not { } progress) return;
        ReleaseStickyProgress();
        SetProgressHint(null);
        if (progress.Items.Children.Count == 0) SetProgressHint("Нет подробностей по этому запросу");
        progress.Finish(title);
        ResetProgressStep();
    }

    void ResetProgressStep()
    {
        _chatView.ToolGroup = null;
        _chatView.ReasoningRow = _chatView.CommentaryRow = null;
    }

    void ShowThinking(int _, bool compressed)
    {
        ResetProgressStep();
        SetProgressHint(compressed ? "Сжатие контекста…" : null);
        if (!compressed) _compactChat?.SetStatus("Размышление…");
    }

    void OnModelProgress(ModelProgress progress)
    {
        UpdateProgressText(ref _chatView.ReasoningRow, progress.Reasoning, "Рассуждения модели");
        UpdateProgressText(ref _chatView.CommentaryRow, progress.Commentary, "Промежуточное сообщение");
    }

    void UpdateProgressText(ref RichTextBox? row, string? markdown, string label)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            if (row != null) _chatView.Progress?.Items.Children.Remove(row);
            row = null;
        }
        else if (row == null) AddProgressItem(row = ProgressText(markdown, label));
        else row.Document = ChatDocument(markdown, "TextMuted");
    }

    void OnAssistantDelta(string? text, bool usesTools)
    {
        if (text == null)
        {
            ClearDraft();
            return;
        }
        _draftText.Append(text);
        _draftIsProgress |= usesTools;
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
            _draftView = SelectableText(text, "Text");
            _draftView.Margin = new(0, 8, 0, 8);
            AutomationProperties.SetName(_draftView, "Ответ агента");
            if (!_draftIsProgress) AppendToChat(_draftView);
        }
        if (_draftIsProgress && _draftView.Parent != EnsureProgress().Items)
        {
            ChatPanel.Children.Remove(_draftView);
            _draftView.Paint(ForegroundProperty, "TextMuted");
            AutomationProperties.SetName(_draftView, "Промежуточное сообщение");
            AddProgressItem(_draftView);
        }
        _draftView.Text = text;
    }

    void ClearDraft(bool interrupted = false)
    {
        _draftTimer?.Stop();
        _compactChat?.ShowDraft(null);
        if (interrupted && _draftText.Length > 0)
        {
            _draftIsProgress = true;
            RenderDraft();
            if (_draftView != null) AutomationProperties.SetName(_draftView, "Незавершённый ответ");
        }
        else if (_draftView?.Parent is Panel parent) parent.Children.Remove(_draftView);
        _draftText.Clear();
        _draftView = null;
        _draftIsProgress = false;
    }

    void OnAssistantText(string markdown, TimeSpan? elapsed, DateTimeOffset sentAt)
    {
        ClearDraft();
        if (elapsed != null)
        {
            ShowFinalAnswer(markdown, elapsed, sentAt);
            SyncCompactChat();
            _compactChat?.SetStatus(string.IsNullOrWhiteSpace(markdown) ? "Запрос завершён" : "Ответ готов");
        }
        else if (!string.IsNullOrWhiteSpace(markdown)) AddProgressItem(ProgressText(markdown, "Промежуточное сообщение"));
    }

    void ShowFinalAnswer(string markdown, TimeSpan? elapsed, DateTimeOffset? sentAt)
    {
        EnsureProgress();
        FinishProgress(elapsed is { } time ? "Выполнено за " + Tokens.Duration(time) : "Выполнено");
        _chatView.Progress = null;
        _chatView.ProgressHint = null;
        if (!string.IsNullOrWhiteSpace(markdown)) AddAssistantMessage(markdown, sentAt);
    }

    RichTextBox ProgressText(string markdown, string label)
    {
        var text = ChatText(markdown, "TextMuted", label);
        text.Margin = new(0, 6, 0, 6);
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
        progress.Plan.Update(items);
    }

    void AddProgressTool(string name, UIElement item)
    {
        if (name == "search_text")
        {
            var progress = EnsureProgress();
            SetProgressHint(null);
            if (progress.Searches == null) _chatView.ToolGroup = null;
            ProgressSection.AddSearch(ref progress.Searches, progress.Items, item);
            return;
        }
        bool commands = name is "run_command" or "run_plugin";
        if (_chatView.ToolGroup is not { } group || group.Commands != commands)
        {
            var section = new ProgressSection(commands ? "Команды" : "Действия", commands ? Glyphs.Terminal : Glyphs.Tool);
            section.View.IsExpanded = true;
            section.View.Margin = new(-3, 2, 0, 2);
            AddProgressItem(section.View);
            _chatView.ToolGroup = group = (section, commands);
        }
        group.Section.Items.Children.Add(item);
    }
}
