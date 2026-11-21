using System.Windows.Shell;

namespace SharpAgent;

public partial class MainWindow
{
    Border ChangesCard(ProducedFile[] files)
    {
        var undoLabel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = Tr("Отменить", "Undo") }, GlyphText(Glyphs.Undo, 12, null, new(6, 0, 0, 0)) },
        };
        var undo = ChangeAction(undoLabel, Tr("Отменить изменения в этой группе", "Undo changes in this group"));
        ToolTipService.SetShowOnDisabled(undo, true);
        var review = ChangeAction(Tr("Просмотреть изменения", "Review changes"), Tr("Открыть список изменений справа", "Open the changes list in the side panel"));
        review.Margin = new(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(8, 0, 0, 0), Children = { undo, review } };
        DockPanel.SetDock(buttons, Dock.Right);
        var heading = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var header = new DockPanel { Children = { buttons, heading } };
        var totals = Totals(files);
        if (files.Any(file => file.Diff != null)) header.Children.Add(totals);
        var rows = new StackPanel { Margin = new(-8, 8, -8, 0) };
        var extra = new StackPanel();
        var reveal = new RevealPanel { Child = extra };
        const int shown = 3;
        var rowButtons = new Button[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            var file = files[i];
            int index = i;
            var title = CardTitle(file.Path, keepExtension: true);
            var row = new DockPanel();
            if (file.Diff != null)
            {
                var count = DiffSummary(file.Diff.Added, file.Diff.Removed);
                DockPanel.SetDock(count, Dock.Right);
                row.Children.Add(count);
            }
            row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = rowButtons[i] = new Button
            {
                Content = row, Style = Theme.Style("RowButton"), Height = 32, Padding = new(8, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = file.Path, ContextMenu = FileActions.BuildMenu(this, file.Path),
            };
            AutomationProperties.SetName(button, Tr($"Просмотреть {title}", $"Review {title}"));
            button.Click += (_, _) => ReviewChanges(files, index);
            (i < shown ? rows : extra).Children.Add(button);
        }
        rows.Children.Add(reveal);
        if (files.Length > shown)
        {
            int remaining = files.Length - shown;
            var expand = Tr($"Показать ещё {remaining} {FileCountWord(remaining)}", $"Show {remaining} more {FileCountWord(remaining)}");
            var toggle = new Button { Style = Theme.Style("RowButton"), Height = 32, Padding = new(8, 0, 8, 0) };
            void UpdateToggle()
            {
                bool opened = reveal.IsOpen;
                var label = opened ? Tr("Свернуть список", "Collapse list") : expand;
                var chevron = GlyphText(Glyphs.ChevronDown, 12, null, new(8, 0, 0, 0));
                chevron.RenderTransformOrigin = new(.5, .5);
                chevron.RenderTransform = opened ? new RotateTransform(180) : Transform.Identity;
                toggle.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = label }, chevron } };
                AutomationProperties.SetName(toggle, label);
            }
            toggle.Click += (_, _) =>
            {
                reveal.IsOpen = !reveal.IsOpen;
                UpdateToggle();
            };
            UpdateToggle();
            rows.Children.Add(toggle);
        }
        void Refresh()
        {
            heading.Text = ChangesHeading(files);
            totals.Visibility = VisibleIf(files.All(file => !file.Undone));
            undo.Visibility = VisibleIf(files.Any(file => !file.Undone));
            undo.IsEnabled = files.Where(file => !file.Undone).All(file => file.Change is { Created: true } or { Backup: not null });
            undo.ToolTip = undo.IsEnabled ? null : Tr("Для части файлов нет резервной копии", "Some files have no backup");
            for (int i = 0; i < files.Length; i++) rowButtons[i].Opacity = files[i].Undone ? .55 : 1;
        }
        undo.Click += async (_, _) =>
        {
            await RunChatOperationAsync(() => UndoChangesAsync(files));
            Refresh();
        };
        review.Click += (_, _) => ReviewGroup(files);
        Refresh();
        return BlockBox(new StackPanel { Children = { header, rows } }, new(12));
    }

    static string FileCountWord(int count) => !UiLanguage.Current.Value.IsRussian ? count == 1 ? "file" : "files" : count % 100 is >= 11 and <= 14 ? "файлов" : (count % 10) switch
    {
        1 => "файл",
        2 or 3 or 4 => "файла",
        _ => "файлов",
    };

    static string ChangesHeading(ProducedFile[] files) => files.Count(file => !file.Undone) switch
    {
        0 => Tr("Изменения отменены", "Changes undone"),
        var remaining when remaining < files.Length => Tr($"Осталось {remaining} {FileCountWord(remaining)}", $"{remaining} {FileCountWord(remaining)} remaining"),
        _ => Tr($"Изменено {files.Length} {FileCountWord(files.Length)}", $"{files.Length} {FileCountWord(files.Length)} changed"),
    };

    static StackPanel Totals(ProducedFile[] files) => DiffSummary(files.Sum(file => file.Diff?.Added ?? 0L), files.Sum(file => file.Diff?.Removed ?? 0L));

    static bool IsChange(ProducedFile file) => file.Change != null || file.Diff != null;

    static Button ChangeAction(object content, string hint)
    {
        var button = new Button { Content = content };
        AutomationProperties.SetName(button, hint);
        return button;
    }

    void ReviewGroup(ProducedFile[] files) => ReviewChanges(files, Math.Max(0, Array.FindIndex(files, file => file.Diff != null)));

    void UpdateChangesPill()
    {
        var files = CurrentRun is { Cancellation.IsCancellationRequested: false, Question: null, Activated: null } &&
            Taskbar.ProgressState != TaskbarItemProgressState.Paused ? TurnFilesForDisplay() : null;
        if (files?.Any(file => !file.Undone && IsChange(file)) != true) files = null;
        ChangesPill.Tag = files;
        ChangesPill.Visibility = VisibleIf(files != null && IsShown(ChatPanel));
        ChangesPill.Content = files == null ? null : ChangesPillContent(files);
        _compactChat?.ShowChanges(files == null ? null : ChangesPillContent(files));
    }

    static StackPanel ChangesPillContent(ProducedFile[] files)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = ChangesHeading(files) } } };
        if (files.All(file => !file.Undone) && files.Any(file => file.Diff != null)) content.Children.Add(Totals(files));
        return content;
    }

    void ReviewChanges(ProducedFile[] files, int selected) => ShowPreview(files[selected].Path, changes: files, selected: selected);

    void ChangesPill_Click(object sender, RoutedEventArgs e)
    {
        if (ChangesPill.Tag is not ProducedFile[] files) return;
        if (_compactChat is { } compact && sender == compact.ChangesPill) compact.Close();
        ReviewGroup(files);
    }

    async Task UndoChangesAsync(ProducedFile[] files)
    {
        if (_cts != null)
        {
            ShowToast(Tr("Дождитесь завершения текущего запроса, чтобы отменить изменения.", "Wait for the current request to finish before undoing changes."), ToastKind.Warning);
            return;
        }
        var pending = files.Where(file => !file.Undone).ToArray();
        if (pending.Length == 0 || pending.Any(file => file.Change == null) || !DarkDialog.Ask(this, Tr("Отменить изменения?", "Undo changes?"),
                Tr("Прежние версии файлов будут восстановлены. Новые файлы переместятся в Корзину.", "Previous versions will be restored. New files will be moved to the Recycle Bin."), Tr("Отменить изменения", "Undo changes"), Tr("Оставить", "Keep"), danger: true))
            return;
        var restored = new List<(FileChange, int)>();
        int restoredFiles = 0;
        using var access = _toolContext.FileAccess.Begin();
        try
        {
            foreach (var file in pending)
            {
                FileChanges.EnsureRestorable(_toolContext, file.Change!);
                _toolContext.FileAccess.Reserve(file.Path);
            }
            foreach (var file in pending)
            {
                FileChanges.Restore(_toolContext, file.Change!);
                files[Array.IndexOf(files, file)] = file with { Undone = true };
                restored.Add((file.Change!, file.Steps));
                restoredFiles++;
            }
            ShowToast(Tr("Изменения отменены", "Changes undone"), ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException)
        {
            ShowToast(restoredFiles > 0 ? Tr($"Восстановлено {restoredFiles} {FileCountWord(restoredFiles)}. {ex.Message}", $"Restored {restoredFiles} {FileCountWord(restoredFiles)}. {ex.Message}") : ex.Message, ToastKind.Warning);
        }
        finally
        {
            if (restored.Count > 0)
            {
                await _agent.MarkFileChangeUndoneAsync(restored);
                if (_previewChanges == null && _previewDiff == null && _previewPath != null &&
                    files.Any(file => file.Undone && PathHelper.SamePath(file.Path, _previewPath))) RenderPreviewBody();
            }
        }
    }
}
