namespace SharpAgent;

public partial class MainWindow
{
    sealed class SubagentRow(SubagentUpdate update)
    {
        public SubagentUpdate State = update;
        public readonly Shimmer Status = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        public readonly TextBlock Placeholder = new TextBlock
        {
            FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new(0, 12, 0, 0),
        }.Paint(ForegroundProperty, "TextMuted");
        StackPanel? _empty;
        public StackPanel Empty => _empty ??= new() { Margin = new(0, 48, 0, 0), Children = { EmptyGlyph(Glyphs.Plugin), Placeholder } };
        public readonly StackPanel Activity = new();
        public readonly Dictionary<string, Shimmer> Tools = new();
        public TextBlock? Commentary;
        public ProgressSection? Searches, Computer;
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
            SubagentsOpenButton.Content = Tr($"Субагенты · {_subagentRows.Count}", $"Subagents · {_subagentRows.Count}");
            SubagentsOpenButton.Visibility = Visibility.Visible;
            UpdateLayoutWidths();
        }
        row.State = update;
        UpdateSubagentStatus(row);
        bool newTool = update.Action is { } next && !row.Tools.ContainsKey(next.Id);
        if (update.Commentary != null || update.Action != null || update.Result != null) row.Activity.Children.Remove(row.Empty);
        if (update.NewStep || newTool) row.Commentary = null;
        if (update.Commentary is { } commentary)
        {
            if (row.Commentary == null)
                row.Activity.Children.Add(row.Commentary = new TextBlock
                {
                    LineHeight = 23, TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 12),
                }.Paint(ForegroundProperty, "Text"));
            if (row.Commentary.Text != commentary) row.Commentary.Text = commentary;
        }
        if (newTool && update.Action is { } call)
        {
            if (!ComputerTools.IsControl(call.Name)) row.Computer = null;
            var description = Describe(call.Name, call.ArgumentsJson);
            var label = call.Name == LocalResearchEngine.Name ? description.Label : Activity(call.Name, call.ArgumentsJson).Trim();
            var text = new Shimmer { Text = label, TextWrapping = TextWrapping.Wrap, IsActive = true };
            if (call.Name == "search_text")
            {
                text.Text = OneLine(ArgumentOf(call.ArgumentsJson, "query"));
                text.Margin = new(0, 3, 0, 3);
                ProgressSection.AddSearch(ref row.Searches, row.Activity, text);
            }
            else
            {
                var item = new DockPanel
                {
                    Margin = new(0, 6, 0, 7),
                    Children = { GlyphText(description.Glyph, 16, "TextMuted", new(0, 2, 10, 0), false), text },
                };
                if (ComputerTools.IsControl(call.Name))
                {
                    row.Computer ??= ProgressSection.Collapsed(Tr("Использование компьютера", "Computer use"), Glyphs.Laptop, row.Activity);
                    row.Computer.Items.Children.Add(item);
                }
                else row.Activity.Children.Add(item);
            }
            row.Tools.Add(call.Id, text);
        }
        if (update.CompletedAction is { } done && row.Tools.Remove(done.Id, out var tool))
        {
            bool failed = done.IsError || done.IsWarning;
            tool.Text = (done.Name is "search_text" or LocalResearchEngine.Name ? tool.Text : Describe(done.Name).Label) + (failed ? " · ошибка" : " · выполнено");
            tool.IsActive = false;
            tool.Base = failed ? "Warning" : "TextMuted";
        }
        if (update.Failed && update.Elapsed != null)
        {
            foreach (var pending in row.Tools.Values)
            {
                pending.Text = Tr("Остановлено · ", "Stopped · ") + pending.Text;
                pending.IsActive = false;
                pending.Base = "Warning";
            }
            row.Tools.Clear();
        }
        if (update.Result is { } result)
        {
            row.Activity.Children.Add(new TextBlock
            {
                Text = update.Failed ? Tr("Ошибка", "Error") : "Результат", Margin = new(0, 8, 0, 12),
            }.Paint(ForegroundProperty, "TextMuted"));
            row.Activity.Children.Add(ChatText(result, update.Failed ? "Warning" : "Text", Tr("Результат субагента", "Subagent result")));
        }
        if (ReferenceEquals(_selectedSubagent, row)) UpdateSubagentHeader(row);
    }

    Button SubagentListRow(SubagentRow row)
    {
        var arrow = GlyphText(Glyphs.ChevronRight, 12, "TextMuted");
        DockPanel.SetDock(arrow, Dock.Right);
        var title = new TextBlock { Text = row.State.Title, TextTrimming = TextTrimming.CharacterEllipsis }.Paint(ForegroundProperty, "Text");
        var button = new Button
        {
            Content = new DockPanel { Children = { arrow, new StackPanel { Margin = new(0, 0, 10, 0), Children = { title, row.Status } } } },
            Style = Theme.Style("RowButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = row.State.Title.Length > 30 ? row.State.Title : null,
        };
        AutomationProperties.SetName(button, Tr("Открыть активность субагента ", "Open activity for subagent ") + row.State.Title);
        button.Click += (_, _) => ShowSubagentPage(row);
        return button;
    }

    static void UpdateSubagentStatus(SubagentRow row)
    {
        var update = row.State;
        var time = update.Elapsed ?? DateTimeOffset.Now - update.Started;
        var status = update.Action is { } action ? Activity(action.Name, action.ArgumentsJson).Trim() : update.Status;
        row.Placeholder.Text = row.Status.Text = time is { } elapsed ? $"{status} · {Tokens.Duration(elapsed)}" : status;
        row.Status.Base = update.Failed ? "Warning" : "TextMuted";
        row.Status.IsActive = update is { Started: not null, Elapsed: null, Failed: false };
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
            ? model + " · " + Tr(effort.Russian, effort.English) : model;
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
            if (row.Activity.Children.Count == 0) row.Activity.Children.Add(row.Empty);
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
        HideBrowser();
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
        AutomationProperties.SetName(SubagentsExpandButton, _subagentsExpanded ? "Свернуть в боковую панель" : Tr("Развернуть на всё рабочее пространство", "Expand to workspace"));
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
