using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SharpAgent.Agent;
using SharpAgent.Core;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private sealed class SubagentRow(SubagentUpdate update)
    {
        public SubagentUpdate State { get; set; } = update;
        public TextBlock Status { get; } = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        public TextBlock Placeholder { get; } = new() { Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap };
        public StackPanel Activity { get; } = new();
        public Dictionary<string, TextBlock> Tools { get; } = new();
        public TextBlock? Commentary { get; set; }
    }

    private readonly Dictionary<string, SubagentRow> _subagentRows = new();
    private SubagentRow? _selectedSubagent;

    private void OnSubagentChanged(SubagentUpdate update)
    {
        if (!_subagentRows.TryGetValue(update.Id, out var row))
        {
            row = new SubagentRow(update);
            _subagentRows.Add(update.Id, row);
            SubagentList.Children.Add(SubagentListRow(row));
            SubagentsOpenButton.Content = $"Субагенты · {_subagentRows.Count}";
            SubagentsOpenButton.Visibility = Visibility.Visible;
            UpdateLayoutWidths();
        }
        row.State = update;
        UpdateSubagentStatus(row);
        bool newTool = update.Action is { } next && !row.Tools.ContainsKey(next.Id);
        if (update.Commentary != null || update.Action != null || update.Result != null)
            row.Activity.Children.Remove(row.Placeholder);
        if (update.NewStep || newTool) row.Commentary = null;
        if (update.Commentary is { } commentary)
        {
            if (row.Commentary == null)
            {
                row.Commentary = new TextBlock
                {
                    Foreground = Theme.Text, LineHeight = 22, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16),
                };
                row.Activity.Children.Add(row.Commentary);
            }
            if (row.Commentary.Text != commentary) row.Commentary.Text = commentary;
        }
        if (newTool && update.Action is { } call)
            AddSubagentTool(row, call);
        if (update.CompletedAction is { } done && row.Tools.Remove(done.Id, out var tool))
        {
            bool failed = done.IsError || done.IsWarning;
            tool.Text = Describe(done.Name).Label + (failed ? " · ошибка" : " · выполнено");
            tool.Foreground = failed ? Theme.Warning : Theme.TextMuted;
        }
        if (update.Failed && update.Elapsed != null)
        {
            foreach (var pending in row.Tools.Values)
            {
                pending.Text = "Остановлено · " + pending.Text;
                pending.Foreground = Theme.Warning;
            }
            row.Tools.Clear();
        }
        if (update.Result is { } result)
        {
            row.Activity.Children.Add(new TextBlock
            {
                Text = update.Failed ? "Ошибка" : "Результат", Foreground = Theme.TextMuted, Margin = new Thickness(0, 8, 0, 12),
            });
            row.Activity.Children.Add(ChatText(result, update.Failed ? Theme.Warning : Theme.Text, "Результат субагента"));
        }
        if (ReferenceEquals(_selectedSubagent, row)) UpdateSubagentHeader(row);
    }

    private Button SubagentListRow(SubagentRow row)
    {
        var details = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        details.Children.Add(new TextBlock
        {
            Text = row.State.Title, Foreground = Theme.Text, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 3),
        });
        details.Children.Add(row.Status);
        var content = new DockPanel { LastChildFill = true };
        var arrow = new TextBlock
        {
            Text = Glyphs.ChevronRight, FontFamily = Theme.Icons, FontSize = 11, Foreground = Theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(arrow, Dock.Right);
        content.Children.Add(arrow);
        content.Children.Add(details);
        var button = new Button
        {
            Content = content, Style = Theme.Style("RowButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = row.State.Title, Margin = new Thickness(0, 0, 0, 4),
        };
        AutomationProperties.SetName(button, "Открыть активность субагента " + row.State.Title);
        button.Click += (_, _) => ShowSubagentPage(row);
        return button;
    }

    private static void AddSubagentTool(SubagentRow row, ToolCallEvent call)
    {
        var label = Activity(call.Name, call.ArgumentsJson).Trim();
        var text = new TextBlock { Text = label, ToolTip = label, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap };
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        line.Children.Add(new TextBlock
        {
            Text = Describe(call.Name).Glyph, FontFamily = Theme.Icons, FontSize = 13,
            Foreground = Theme.TextMuted, Margin = new Thickness(0, 2, 8, 0),
        });
        line.Children.Add(text);
        row.Activity.Children.Add(line);
        row.Tools.Add(call.Id, text);
    }

    private static void UpdateSubagentStatus(SubagentRow row)
    {
        var update = row.State;
        var time = update.Elapsed ?? (update.Started is { } started ? DateTimeOffset.Now - started : (TimeSpan?)null);
        var status = update.Action is { } action ? Activity(action.Name, action.ArgumentsJson).Trim() : update.Status;
        row.Status.Text = time is { } elapsed ? $"{status} · {Tokens.Duration(elapsed)}" : status;
        row.Status.Foreground = update.Failed ? Theme.Warning : Theme.TextMuted;
        row.Placeholder.Text = row.Status.Text;
    }

    private void UpdateSubagentTimes()
    {
        foreach (var row in _subagentRows.Values)
            if (row.State is { Started: not null, Elapsed: null }) UpdateSubagentStatus(row);
        if (_selectedSubagent is { } selected) UpdateSubagentHeader(selected);
    }

    private void UpdateSubagentHeader(SubagentRow row)
    {
        var state = row.State;
        SubagentTitleText.Text = state.Title;
        var model = state.Model == null ? "" : _modelNames.GetValueOrDefault(state.Provider)?.GetValueOrDefault(state.Model) ?? state.Model;
        SubagentModelText.Text = state.Effort is { } key && EffortNames.TryGetValue(key, out var effort) && model.Length > 0
            ? model + " · " + effort.Label : model;
        SubagentTitleText.ToolTip = row.Status.Text;
    }

    private void ShowSubagentPage(SubagentRow? row)
    {
        _selectedSubagent = row;
        SubagentList.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
        SubagentBackButton.Visibility = SubagentDetail.Visibility = row != null ? Visibility.Visible : Visibility.Collapsed;
        SubagentActivity.Content = null;
        if (row != null)
        {
            if (row.Activity.Children.Count == 0) row.Activity.Children.Add(row.Placeholder);
            SubagentActivity.Content = row.Activity;
            UpdateSubagentHeader(row);
        }
        else
        {
            SubagentTitleText.Text = "Субагенты";
            SubagentTitleText.ToolTip = null;
            SubagentModelText.Text = "";
        }
        SubagentsScroll.ScrollToTop();
    }

    private void SubagentsOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_subagentRows.Count == 0) return;
        if (SubagentsWorkspace.Visibility == Visibility.Visible)
        {
            CloseSubagents();
            return;
        }
        SubagentsWorkspace.Visibility = Visibility.Visible;
        ShowSubagentPage(_selectedSubagent ?? (_subagentRows.Count == 1 ? _subagentRows.Values.First() : null));
        SubagentsCloseButton.Focus();
    }

    private void SubagentBack_Click(object sender, RoutedEventArgs e)
    {
        ShowSubagentPage(null);
        (SubagentList.Children.OfType<Button>().FirstOrDefault() ?? SubagentsCloseButton).Focus();
    }

    private void SubagentsClose_Click(object sender, RoutedEventArgs e) => CloseSubagents();

    private void CloseSubagents()
    {
        if (SubagentsWorkspace.Visibility != Visibility.Visible) return;
        SubagentsWorkspace.Visibility = Visibility.Collapsed;
        InputBox.Focus();
    }

    private void ClearSubagents()
    {
        if (_subagentRows.Count == 0) return;
        CloseSubagents();
        ShowSubagentPage(null);
        _subagentRows.Clear();
        SubagentList.Children.Clear();
        SubagentsOpenButton.Visibility = Visibility.Collapsed;
        UpdateLayoutWidths();
    }
}
