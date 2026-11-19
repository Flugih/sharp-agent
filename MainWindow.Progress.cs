using System.Windows.Documents;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    sealed class ProgressSection
    {
        readonly Shimmer _label = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        readonly DockPanel _header = new() { MinHeight = 25 };
        TextBlock? _error;
        int _errors;
        public readonly StackPanel Items = new();
        public readonly Expander View = new() { Style = Theme.Style("ProgressSection") };
        public PlanCard? Plan;
        public ProgressSection? Searches;

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

        public void SetTitle(string title, string? label = null)
        {
            _label.Text = label ?? title;
            AutomationProperties.SetName(View, title);
            View.ToolTip = title + " · раскрыть или свернуть";
        }

        public void ShowError(string text)
        {
            if (_error == null)
            {
                _error = new TextBlock { Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }.Paint(ForegroundProperty, "DangerText");
                _header.Children.Add(_error);
            }
            _error.Text = ++_errors > 1 ? $"Ошибок: {_errors} · {text}" : text;
        }

        public static ProgressSection Collapsed(string title, string glyph, Panel parent)
        {
            var group = new ProgressSection(title, glyph);
            parent.Children.Add(group.View);
            return group;
        }

        public static void AddSearch(ref ProgressSection? searches, Panel parent, UIElement item)
        {
            searches ??= Collapsed("Поиск текста", Glyphs.Search, parent);
            searches.Items.Children.Add(item);
            searches.SetTitle($"Поиск текста · {searches.Items.Children.Count}");
        }

        public void Start()
        {
            SetTitle("Думаю…");
            _label.IsActive = View.IsExpanded = true;
        }

        public void Finish(string title)
        {
            SetTitle(title);
            _label.IsActive = false;
            View.IsExpanded = false;
        }
    }

    sealed class PlanCard
    {
        readonly ProgressSection _section = new("", Glyphs.Check) { View = { IsExpanded = true } };
        readonly ProgressBar _meter = new() { Style = Theme.Style("Meter"), Margin = new(0, 8, 0, 6) };
        readonly StackPanel _tasks = new();
        public FrameworkElement View => _section.View;

        public PlanCard()
        {
            _section.Items.Children.Add(_meter);
            _section.Items.Children.Add(_tasks);
        }

        public void Update(IReadOnlyList<AgentRunner.PlanItem> items)
        {
            int done = items.Count(item => item.Done);
            var step = items.FirstOrDefault(item => item.Status == "in_progress").Step ?? items.FirstOrDefault(item => !item.Done).Step ?? "План выполнен";
            var summary = $"План. Выполнено {done} из {items.Count}";
            _section.SetTitle(summary, $"{done}/{items.Count} · {step}");
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
                    VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 2, 8, 0),
                };
                AutomationProperties.SetName(check, item.Step);
                DockPanel.SetDock(check, Dock.Left);
                var text = new TextBlock
                {
                    Text = item.Step, TextWrapping = TextWrapping.Wrap, TextDecorations = item.Done ? TextDecorations.Strikethrough : null,
                };
                text.SetResourceReference(TextBlock.ForegroundProperty, active ? "Text" : "TextMuted");
                var row = new DockPanel { Margin = new(0, 6, 0, 6), Children = { check, text } };
                if (active)
                {
                    var status = new TextBlock
                    {
                        Text = "В работе", FontSize = 13, Margin = new(10, 1, 0, 0),
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
    RichTextBox? _draftView;
    int _draftShown;
    bool _draftIsProgress;

    ProgressSection EnsureProgress()
    {
        if (_chatView.Progress is { } progress) return progress;
        progress = _chatView.Progress = new("Ход работы");
        progress.View.Margin = new(0, 8, 0, 8);
        AppendToChat(progress.View);
        return progress;
    }

    void StartProgress()
    {
        ResetProgressStep();
        EnsureProgress().Start();
        SetProgressHint("Подготовка запроса…");
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
        if (!compressed) _compactChat?.SetStatus("Думаю…");
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
        else row.Document = ChatDocument(markdown, ProgressColor(label));
    }

    // Reasoning stays quiet; interim messages read like the answer.
    static string ProgressColor(string label) => label == "Рассуждения модели" ? "TextMuted" : "Text";

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
            var document = new FlowDocument(new Paragraph(new Run()) { Margin = new(0) })
            {
                PagePadding = new(0), FontFamily = Theme.Ui, FontSize = 14, LineHeight = 23,
            }.Paint(TextElement.ForegroundProperty, "Text");
            _draftView = ReadOnlyChatText(document, "Ответ агента");
            _draftView.Margin = new(-5, 8, -5, 8);
            _draftView.ContextMenu = (ContextMenu)Application.Current.Resources["ReadOnlyTextMenu"];
            _draftShown = 0;
            if (!_draftIsProgress) AppendToChat(_draftView);
        }
        if (_draftIsProgress && _draftView.Parent != EnsureProgress().Items)
        {
            ChatPanel.Children.Remove(_draftView);
            AutomationProperties.SetName(_draftView, "Промежуточное сообщение");
            AddProgressItem(_draftView);
        }
        AppendDraft(text[_draftShown..]);
        _draftShown = text.Length;
    }

    // Each chunk fades in as its own Run and then joins the first plain Run, so a long stream keeps no brushes or clocks.
    void AppendDraft(string chunk)
    {
        if (chunk.Length == 0) return;
        var paragraph = (Paragraph)_draftView!.Document.Blocks.FirstBlock;
        var settled = (Run)paragraph.Inlines.FirstInline;
        if (!Theme.AnimationsEnabled)
        {
            settled.ContentEnd.InsertTextInRun(chunk);
            return;
        }
        var brush = new SolidColorBrush(((SolidColorBrush)Theme.Text).Color);
        var run = new Run(chunk) { Foreground = brush };
        paragraph.Inlines.Add(run);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            settled.ContentEnd.InsertTextInRun(run.Text);
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
            if (_draftView != null) AutomationProperties.SetName(_draftView, "Незавершённый ответ");
        }
        else if (_draftView?.Parent is Panel parent) parent.Children.Remove(_draftView);
        _draftText.Clear();
        _draftView = null;
        _draftIsProgress = false;
        _compactChat?.ShowDraft(partial);
    }

    void OnAssistantText(string markdown, TimeSpan? elapsed, DateTimeOffset sentAt)
    {
        ClearDraft();
        if (elapsed != null)
        {
            ShowFinalAnswer(markdown, elapsed, sentAt);
            SyncCompactChat();
            _compactChat?.SetStatus(string.IsNullOrWhiteSpace(markdown) ? "Запрос завершён" : "Ответ готов", "Success");
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
        var text = ChatText(markdown, ProgressColor(label), label);
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
            var section = new ProgressSection(commands ? "Команды" : "Действия", commands ? Glyphs.Terminal : Glyphs.Tool) { View = { IsExpanded = true } };
            AddProgressItem(section.View);
            _chatView.ToolGroup = group = (section, commands);
        }
        group.Section.Items.Children.Add(item);
    }
}
