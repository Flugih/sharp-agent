namespace SharpAgent;

public partial class MainWindow
{
    sealed class ProgressSection
    {
        readonly TextBlock _label = new() { FontSize = 13, Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center };
        readonly StackPanel _header = new() { Orientation = Orientation.Horizontal };
        FrameworkElement? _spinner;
        public readonly StackPanel Items = new();
        public readonly Expander View = new() { Style = Theme.Style("ProgressSection") };

        public ProgressSection(string title, string? glyph = null)
        {
            if (glyph != null) _header.Children.Add(GlyphText(glyph, 13, Theme.TextMuted, new(0, 0, 8, 0)));
            _header.Children.Add(_label);
            View.Header = _header;
            View.Content = glyph == null ? Items : new Border
            {
                BorderBrush = Theme.Line, BorderThickness = new(1, 0, 0, 0), Margin = new(11, 2, 0, 3),
                Padding = new(13, 0, 0, 0), Child = Items,
            };
            SetTitle(title);
        }

        void SetTitle(string title)
        {
            _label.Text = title;
            AutomationProperties.SetName(View, title);
            View.ToolTip = title + " · раскрыть или свернуть";
        }

        public void Start()
        {
            SetTitle("Думаю…");
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
        _chatView.ProgressHint = new TextBlock { Text = text, FontSize = 13, Foreground = Theme.TextMuted, Margin = new(0, 5, 0, 5) };
        EnsureProgress().Items.Children.Add(_chatView.ProgressHint);
    }

    void FinishProgress(string title)
    {
        if (_chatView.Progress is not { } progress) return;
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
        else row.Document = ChatDocument(markdown, Theme.TextMuted);
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
            _draftView = SelectableText(text, Theme.Text);
            _draftView.Margin = new(0, 8, 0, 8);
            AutomationProperties.SetName(_draftView, "Ответ агента");
            if (!_draftIsProgress) AppendToChat(_draftView);
        }
        if (_draftIsProgress && _draftView.Parent != EnsureProgress().Items)
        {
            ChatPanel.Children.Remove(_draftView);
            _draftView.Foreground = Theme.TextMuted;
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
        var text = ChatText(markdown, Theme.TextMuted, label);
        text.Margin = new(0, 6, 0, 6);
        return text;
    }

    void AddProgressItem(UIElement item)
    {
        SetProgressHint(null);
        EnsureProgress().Items.Children.Add(item);
    }

    void AddProgressTool(string name, UIElement item)
    {
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
