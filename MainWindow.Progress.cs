using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private sealed class ProgressSection
    {
        private readonly TextBlock _label = new() { FontSize = 13, Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center };
        private readonly StackPanel _header = new() { Orientation = Orientation.Horizontal };
        public StackPanel Items { get; } = new();
        public Expander View { get; } = new() { Style = Theme.Style("ProgressSection") };
        private FrameworkElement? _spinner;

        public ProgressSection(string title, string? glyph = null)
        {
            if (glyph != null)
                _header.Children.Add(new TextBlock
                {
                    Text = glyph, FontFamily = Theme.Icons, FontSize = 13, Foreground = Theme.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                });
            _header.Children.Add(_label);
            View.Header = _header;
            View.Content = glyph == null ? Items : new Border
            {
                BorderBrush = Theme.Line, BorderThickness = new Thickness(1, 0, 0, 0),
                Margin = new Thickness(11, 2, 0, 3), Padding = new Thickness(13, 0, 0, 0),
                Child = Items,
            };
            SetTitle(title);
        }

        private void SetTitle(string title)
        {
            _label.Text = title;
            AutomationProperties.SetName(View, title);
            View.ToolTip = title + " · раскрыть или свернуть";
        }

        public void Start()
        {
            SetTitle("Думаю…");
            _spinner = MakeSpinner(12);
            _spinner.Margin = new Thickness(0, 0, 8, 0);
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

    private readonly StringBuilder _draftText = new();
    private DispatcherTimer? _draftTimer;
    private TextBox? _draftView;
    private bool _draftIsProgress;

    private ProgressSection EnsureProgress()
    {
        if (_chatView.Progress != null) return _chatView.Progress;
        _chatView.Progress = new ProgressSection("Ход работы");
        _chatView.Progress.View.Margin = new Thickness(-3, 8, 0, 0);
        _chatView.Progress.Items.Margin = new Thickness(3, 2, 0, 2);
        AppendToChat(_chatView.Progress.View);
        return _chatView.Progress;
    }

    private void StartProgress()
    {
        ResetProgressStep();
        EnsureProgress().Start();
        SetProgressHint("Подготовка запроса…");
    }

    private void SetProgressHint(string? text)
    {
        if (_chatView.ProgressHint != null)
        {
            _chatView.Progress?.Items.Children.Remove(_chatView.ProgressHint);
            _chatView.ProgressHint = null;
        }
        if (text == null) return;
        _chatView.ProgressHint = new TextBlock { Text = text, FontSize = 13, Foreground = Theme.TextMuted, Margin = new Thickness(0, 5, 0, 5) };
        EnsureProgress().Items.Children.Add(_chatView.ProgressHint);
    }

    private void FinishProgress(string title)
    {
        if (_chatView.Progress == null) return;
        SetProgressHint(null);
        if (_chatView.Progress.Items.Children.Count == 0) SetProgressHint("Нет подробностей по этому запросу");
        _chatView.Progress.Finish(title);
        ResetProgressStep();
    }

    private void ResetProgressStep()
    {
        _chatView.ToolGroup = null;
        _chatView.ReasoningRow = null;
        _chatView.CommentaryRow = null;
    }

    private void ShowThinking(int _, bool compressed)
    {
        ResetProgressStep();
        SetProgressHint(compressed ? "Сжатие контекста…" : null);
    }

    private void OnModelProgress(ModelProgress progress)
    {
        UpdateProgressText(ref _chatView.ReasoningRow, progress.Reasoning, "Рассуждения модели");
        UpdateProgressText(ref _chatView.CommentaryRow, progress.Commentary, "Промежуточное сообщение");
    }

    private void UpdateProgressText(ref RichTextBox? row, string? markdown, string label)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            if (row != null) _chatView.Progress?.Items.Children.Remove(row);
            row = null;
            return;
        }
        if (row == null)
        {
            row = ProgressText(markdown, label);
            AddProgressItem(row);
        }
        else
        {
            row.Document = ChatDocument(markdown, Theme.TextMuted);
        }
    }

    private void OnAssistantDelta(string? text, bool usesTools)
    {
        if (text == null)
        {
            ClearDraft();
            return;
        }
        _draftText.Append(text);
        _draftIsProgress |= usesTools;
        if (_draftTimer == null)
        {
            _draftTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            _draftTimer.Tick += (_, _) =>
            {
                _draftTimer.Stop();
                RenderDraft();
            };
        }
        _draftTimer.Start();
    }

    private void RenderDraft()
    {
        var text = _draftText.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_draftView == null)
        {
            _draftView = SelectableText(text, Theme.Text);
            _draftView.Margin = new Thickness(0, 8, 0, 8);
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

    private void ClearDraft(bool interrupted = false)
    {
        _draftTimer?.Stop();
        if (interrupted && _draftText.Length > 0)
        {
            _draftIsProgress = true;
            RenderDraft();
            if (_draftView != null) AutomationProperties.SetName(_draftView, "Незавершённый ответ");
        }
        else if (_draftView?.Parent is Panel parent)
        {
            parent.Children.Remove(_draftView);
        }
        _draftText.Clear();
        _draftView = null;
        _draftIsProgress = false;
    }

    private void OnAssistantText(string markdown, TimeSpan? elapsed)
    {
        ClearDraft();
        if (elapsed == null)
        {
            if (!string.IsNullOrWhiteSpace(markdown))
                AddProgressItem(ProgressText(markdown, "Промежуточное сообщение"));
            return;
        }
        ShowFinalAnswer(markdown, elapsed);
    }

    private void ShowFinalAnswer(string markdown, TimeSpan? elapsed)
    {
        EnsureProgress();
        FinishProgress(elapsed is { } time ? "Выполнено за " + Tokens.Duration(time) : "Выполнено");
        _chatView.Progress = null;
        _chatView.ProgressHint = null;
        if (!string.IsNullOrWhiteSpace(markdown)) AddAssistantMessage(markdown);
    }

    private RichTextBox ProgressText(string markdown, string label)
    {
        var text = ChatText(markdown, Theme.TextMuted, label);
        text.Margin = new Thickness(0, 6, 0, 6);
        return text;
    }

    private void AddProgressItem(UIElement item)
    {
        SetProgressHint(null);
        EnsureProgress().Items.Children.Add(item);
    }

    private void AddProgressTool(string name, UIElement item)
    {
        bool commands = name is "run_command" or "run_plugin";
        if (_chatView.ToolGroup is not { } group || group.Commands != commands)
        {
            var section = new ProgressSection(commands ? "Команды" : "Действия", commands ? Glyphs.Terminal : Glyphs.Tool);
            section.View.IsExpanded = true;
            section.View.Margin = new Thickness(-3, 2, 0, 2);
            AddProgressItem(section.View);
            _chatView.ToolGroup = group = (section, commands);
        }
        group.Section.Items.Children.Add(item);
    }
}
