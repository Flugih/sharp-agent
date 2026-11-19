namespace SharpAgent;

public partial class MainWindow
{
    int _gitBranchRequest;

    async Task<LocalProjectContext.GitBranches?> RefreshGitBranchAsync(bool openMenu = false)
    {
        int request = ++_gitBranchRequest;
        var folder = _toolContext.WorkingDirectory;
        LocalProjectContext.GitBranches? branches = null;
        try { branches = await Task.Run(() => LocalProjectContext.ReadBranchesAsync(_toolContext, folder, openMenu)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException or ArgumentException)
        {
            if (openMenu && request == _gitBranchRequest) ShowToast(ex.Message, ToastKind.Warning);
            return null;
        }
        if (request != _gitBranchRequest || _waitingToClose) return null;
        var current = branches?.Current;
        GitBranchButton.Visibility = VisibleIf(current != null);
        GitBranchText.Text = current ?? "";
        if (current != null) SetHint(GitBranchButton, $"Ветка Git: {current}. Выбрать ветку");
        UpdateLayoutWidths();
        return branches;
    }

    async void GitBranch_Click(object sender, RoutedEventArgs e)
    {
        if (WaitForChatFile(ChatFileBusy)) return;
        var folder = _toolContext.WorkingDirectory;
        if (await RefreshGitBranchAsync(openMenu: true) is not { } branches || !GitBranchButton.IsVisible || !IsActive) return;
        var menu = new ContextMenu { PlacementTarget = GitBranchButton, Placement = PlacementMode.Bottom, MaxHeight = 360, MaxWidth = 480 };
        foreach (var branch in branches.Names.Prepend(branches.Current).Distinct(StringComparer.Ordinal))
        {
            var item = FileActions.Item(branch.Replace("_", "__"), null,
                () => _ = RunChatOperationAsync(() => SwitchGitBranchAsync(folder, branches.Root, branch)));
            item.IsChecked = branch == branches.Current;
            item.ToolTip = branch;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    async Task SwitchGitBranchAsync(string folder, string root, string branch)
    {
        if (AnyRunning)
        {
            ShowToast("Переключение ветки доступно после завершения задач агента.", ToastKind.Warning);
            return;
        }
        if (!PathHelper.SamePath(folder, _toolContext.WorkingDirectory)) return;
        try
        {
            var error = await Task.Run(() => LocalProjectContext.SwitchBranchAsync(_toolContext, folder, root, branch));
            if (error != null) ShowToast("Не удалось переключить ветку. " + error, ToastKind.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException or ArgumentException)
        {
            ShowToast("Не удалось переключить ветку. " + ex.Message, ToastKind.Error);
        }
        await RefreshGitBranchAsync();
    }
}
