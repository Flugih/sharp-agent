namespace SharpAgent;

public partial class MainWindow
{
    sealed class SubagentRow(SubagentUpdate update)
    {
        public SubagentUpdate State = update;
        public readonly TextBlock Status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        public readonly TextBlock Placeholder = new() { Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap };
        public readonly StackPanel Activity = new();
        public readonly Dictionary<string, TextBlock> Tools = new();
        public TextBlock? Commentary;
    }

    readonly Dictionary<string, SubagentRow> _subagentRows = new();
    SubagentRow? _selectedSubagent;
    bool _subagentsExpanded;

    void OnSubagentChanged(SubagentUpdate update)
    {
        if (!_subagentRows.TryGetValue(update.Id, out var row))
        {
            _subagentRows.Add(update.Id, row = new SubagentRow(update));
            SubagentList.Children.Add(SubagentListRow(row));
            SubagentsOpenButton.Content = $"Субагенты · {_subagentRows.Count}";
            SubagentsOpenButton.Visibility = Visibility.Visible;
            UpdateLayoutWidths();
        }
        row.State = update;
        UpdateSubagentStatus(row);
        bool newTool = update.Action is { } next && !row.Tools.ContainsKey(next.Id);
        if (update.Commentary != null || update.Action != null || update.Result != null) row.Activity.Children.Remove(row.Placeholder);
        if (update.NewStep || newTool) row.Commentary = null;
        if (update.Commentary is { } commentary)
        {
            if (row.Commentary == null)
                row.Activity.Children.Add(row.Commentary = new TextBlock
                {
                    Foreground = Theme.Text, LineHeight = 22, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16),
                });
            if (row.Commentary.Text != commentary) row.Commentary.Text = commentary;
        }
        if (newTool && update.Action is { } call)
        {
            var label = Activity(call.Name, call.ArgumentsJson).Trim();
            var text = new TextBlock { Text = label, ToolTip = label, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap };
            row.Activity.Children.Add(new DockPanel
            {
                Margin = new(0, 0, 0, 14),
                Children = { GlyphText(Describe(call.Name).Glyph, 13, Theme.TextMuted, new(0, 2, 8, 0), false), text },
            });
            row.Tools.Add(call.Id, text);
        }
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
                Text = update.Failed ? "Ошибка" : "Результат", Foreground = Theme.TextMuted, Margin = new(0, 8, 0, 12),
            });
            row.Activity.Children.Add(ChatText(result, update.Failed ? Theme.Warning : Theme.Text, "Результат субагента"));
        }
        if (ReferenceEquals(_selectedSubagent, row)) UpdateSubagentHeader(row);
    }

    Button SubagentListRow(SubagentRow row)
    {
        var arrow = GlyphText(Glyphs.ChevronRight, 11, Theme.TextMuted);
        DockPanel.SetDock(arrow, Dock.Right);
        var title = new TextBlock
        {
            Text = row.State.Title, Foreground = Theme.Text, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new(0, 0, 0, 3),
        };
        var button = new Button
        {
            Content = new DockPanel { Children = { arrow, new StackPanel { Margin = new(0, 0, 10, 0), Children = { title, row.Status } } } },
            Style = Theme.Style("RowButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = row.State.Title,
            Margin = new(0, 0, 0, 4),
        };
        AutomationProperties.SetName(button, "Открыть активность субагента " + row.State.Title);
        button.Click += (_, _) => ShowSubagentPage(row);
        return button;
    }

    static void UpdateSubagentStatus(SubagentRow row)
    {
        var update = row.State;
        var time = update.Elapsed ?? DateTimeOffset.Now - update.Started;
        var status = update.Action is { } action ? Activity(action.Name, action.ArgumentsJson).Trim() : update.Status;
        row.Placeholder.Text = row.Status.Text = time is { } elapsed ? $"{status} · {Tokens.Duration(elapsed)}" : status;
        row.Status.Foreground = update.Failed ? Theme.Warning : Theme.TextMuted;
    }

    void UpdateSubagentTimes()
    {
        foreach (var row in _subagentRows.Values)
            if (row.State is { Started: not null, Elapsed: null }) UpdateSubagentStatus(row);
        if (_selectedSubagent is { } selected) UpdateSubagentHeader(selected);
    }

    void UpdateSubagentHeader(SubagentRow row)
    {
        var state = row.State;
        SubagentTitleText.Text = state.Title;
        var model = state.Model == null ? "" : _modelNames.GetValueOrDefault(state.Provider)?.GetValueOrDefault(state.Model) ?? state.Model;
        SubagentModelText.Text = state.Effort is { } key && EffortNames.TryGetValue(key, out var effort) && model.Length > 0
            ? model + " · " + effort.Label : model;
        SubagentTitleText.ToolTip = row.Status.Text;
    }

    void ShowSubagentPage(SubagentRow? row)
    {
        _selectedSubagent = row;
        SubagentList.Visibility = VisibleIf(row == null);
        SubagentHeader.Visibility = SubagentBackButton.Visibility = SubagentDetail.Visibility = VisibleIf(row != null);
        SubagentActivity.Content = null;
        if (row != null)
        {
            if (row.Activity.Children.Count == 0) row.Activity.Children.Add(row.Placeholder);
            SubagentActivity.Content = row.Activity;
            UpdateSubagentHeader(row);
        }
        SubagentsScroll.ScrollToTop();
    }

    void SubagentsOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_subagentRows.Count == 0) return;
        if (IsShown(SubagentsWorkspace))
        {
            CloseSubagents();
            return;
        }
        SubagentsWorkspace.Visibility = Visibility.Visible;
        UpdateSubagentsLayout();
        ApplyPreviewLayout();
        ShowSubagentPage(_selectedSubagent ?? (_subagentRows.Count == 1 ? _subagentRows.Values.First() : null));
        SubagentsCloseButton.Focus();
    }

    void SubagentsExpand_Click(object sender, RoutedEventArgs e)
    {
        _subagentsExpanded = !_subagentsExpanded;
        UpdateSubagentsLayout();
        ApplyPreviewLayout();
    }

    void UpdateSubagentsLayout()
    {
        SubagentsExpandButton.Content = _subagentsExpanded ? Glyphs.BackToWindow : Glyphs.FullScreen;
        SetHint(SubagentsExpandButton, _subagentsExpanded ? "Свернуть в боковую панель" : "Развернуть на всё рабочее пространство");
        KeyboardNavigation.SetTabNavigation(SubagentsWorkspace, _subagentsExpanded ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.Continue);
    }

    void SubagentBack_Click(object sender, RoutedEventArgs e)
    {
        ShowSubagentPage(null);
        (SubagentList.Children.OfType<Button>().FirstOrDefault() ?? SubagentsCloseButton).Focus();
    }

    void SubagentsClose_Click(object sender, RoutedEventArgs e) => CloseSubagents();

    void CloseSubagents(bool focusChat = true)
    {
        if (!IsShown(SubagentsWorkspace)) return;
        SubagentsWorkspace.Visibility = Visibility.Collapsed;
        _subagentsExpanded = false;
        UpdateSubagentsLayout();
        ApplyPreviewLayout();
        if (focusChat) InputBox.Focus();
    }

    void ClearSubagents()
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
