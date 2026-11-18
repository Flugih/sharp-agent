namespace SharpAgent;

public partial class MainWindow
{
    Border ChangesCard(ProducedFile[] files)
    {
        var undoLabel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = "Отменить" }, GlyphText(Glyphs.Undo, 10, null, new(5, 0, 0, 0)) },
        };
        var undo = ChangeAction(undoLabel, "Отменить изменения в этой группе");
        undo.Background = undo.BorderBrush = Brushes.Transparent;
        ToolTipService.SetShowOnDisabled(undo, true);
        var review = ChangeAction("Просмотреть изменения", "Открыть список изменений справа");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { undo, review } };
        DockPanel.SetDock(buttons, Dock.Right);
        var heading = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0), Children = { heading } };
        var totals = DiffSummary(files.Sum(file => file.Diff?.Added ?? 0L), files.Sum(file => file.Diff?.Removed ?? 0L));
        totals.Margin = new(0, 3, 0, 0);
        totals.HorizontalAlignment = HorizontalAlignment.Left;
        if (files.Any(file => file.Diff != null)) caption.Children.Add(totals);
        var glyph = GlyphText(Glyphs.Edit, 17, "TextMuted");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var icon = new Border
        {
            Width = 40, Height = 40, CornerRadius = new(9), Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Child = glyph,
        }.Paint(BackgroundProperty, "SidebarBg");
        var rows = new StackPanel().Paint(BackgroundProperty, "Bg");
        var extra = new StackPanel();
        var reveal = new RevealPanel { Child = extra };
        var list = new StackPanel
        {
            Children =
            {
                new DockPanel { Margin = new(12, 10, 12, 10), Children = { icon, buttons, caption } },
                new Border { BorderThickness = new(0, 1, 0, 0), Child = rows }.Paint(BorderBrushProperty, "Line"),
            },
        };
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
                Content = row, Style = Theme.Style("RowButton"), Height = 36, FontSize = 13, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = file.Path, ContextMenu = FileActions.BuildMenu(this, file.Path),
            };
            AutomationProperties.SetName(button, $"Просмотреть {title}");
            button.Click += (_, _) => ReviewChanges(files, index);
            (i < shown ? rows : extra).Children.Add(button);
        }
        rows.Children.Add(reveal);
        if (files.Length > shown)
        {
            int remaining = files.Length - shown;
            var expand = $"Показать ещё {remaining} {FileCountWord(remaining)}";
            var toggle = new Button { Style = Theme.Style("RowButton"), Height = 35, FontSize = 13 };
            void UpdateToggle()
            {
                bool opened = reveal.IsOpen;
                var label = opened ? "Свернуть список" : expand;
                var chevron = GlyphText(Glyphs.ChevronDown, 10, null, new(8, 0, 0, 0));
                chevron.RenderTransformOrigin = new(.5, .5);
                chevron.RenderTransform = opened ? new RotateTransform(180) : Transform.Identity;
                toggle.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = label }, chevron } };
                toggle.ToolTip = label;
                AutomationProperties.SetName(toggle, label);
            }
            toggle.Click += (_, _) =>
            {
                reveal.IsOpen = !reveal.IsOpen;
                UpdateToggle();
            };
            UpdateToggle();
            list.Children.Add(toggle);
        }
        void Refresh()
        {
            int remaining = files.Count(file => !file.Undone);
            heading.Text = remaining == 0 ? "Изменения отменены"
                : remaining < files.Length ? $"Осталось {remaining} {FileCountWord(remaining)}" : $"Изменено {files.Length} {FileCountWord(files.Length)}";
            totals.Visibility = VisibleIf(remaining == files.Length);
            undo.Visibility = VisibleIf(remaining != 0);
            undo.IsEnabled = files.Where(file => !file.Undone).All(file => file.Change is { Created: true } or { Backup: not null });
            undo.ToolTip = undo.IsEnabled ? "Отменить изменения в этой группе" : "Для части файлов нет резервной копии";
            for (int i = 0; i < files.Length; i++) rowButtons[i].Opacity = files[i].Undone ? .55 : 1;
        }
        undo.Click += async (_, _) =>
        {
            await RunChatOperationAsync(() => UndoChangesAsync(files));
            Refresh();
        };
        review.Click += (_, _) => ReviewChanges(files, Math.Max(0, Array.FindIndex(files, file => file.Diff != null)));
        Refresh();
        return SurfaceBox(list, 10);
    }

    static string FileCountWord(int count) => count % 100 is >= 11 and <= 14 ? "файлов" : (count % 10) switch
    {
        1 => "файл",
        2 or 3 or 4 => "файла",
        _ => "файлов",
    };

    static Button ChangeAction(object content, string hint)
    {
        var button = new Button { Content = content, ToolTip = hint, Height = 28, FontSize = 12, Padding = new(8, 0, 8, 0) };
        AutomationProperties.SetName(button, hint);
        return button;
    }

    void ReviewChanges(ProducedFile[] files, int selected)
    {
        var file = files[selected];
        ShowPreview(file.Path, file.Diff, review: true);
        PreviewChangePicker.Content = new TextBlock
        {
            Text = $"{selected + 1}/{files.Length} · {CardTitle(file.Path, keepExtension: true)}", TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var picker = new ContextMenu { PlacementTarget = PreviewChangePicker, Placement = PlacementMode.Bottom, MaxHeight = 320 };
        for (int i = 0; i < files.Length; i++)
        {
            int index = i;
            var item = FileActions.Item(CardTitle(files[i].Path, keepExtension: true).Replace("_", "__"), i == selected ? Glyphs.Check : null,
                () => ReviewChanges(files, index));
            item.ToolTip = files[i].Path;
            picker.Items.Add(item);
        }
        PreviewChangePicker.ContextMenu = picker;
    }

    void PreviewChangePicker_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewChangePicker.ContextMenu is { } menu) menu.IsOpen = true;
    }

    async Task UndoChangesAsync(ProducedFile[] files)
    {
        if (_cts != null)
        {
            ShowToast("Дождитесь завершения текущего запроса, чтобы отменить изменения.", ToastKind.Warning);
            return;
        }
        var pending = files.Where(file => !file.Undone).ToArray();
        if (pending.Length == 0 || pending.Any(file => file.Change == null) || !DarkDialog.Ask(this, "Отменить изменения?",
                "Прежние версии файлов будут восстановлены. Новые файлы переместятся в Корзину.", "Отменить изменения", "Оставить", danger: true))
            return;
        var restored = new List<(FileChange, int)>();
        int restoredFiles = 0;
        try
        {
            foreach (var file in pending) FileChanges.EnsureRestorable(_toolContext, file.Change!);
            foreach (var file in pending)
            {
                FileChanges.Restore(_toolContext, file.Change!);
                files[Array.IndexOf(files, file)] = file with { Undone = true };
                restored.Add((file.Change!, file.Steps));
                restoredFiles++;
            }
            ShowToast("Изменения отменены", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException)
        {
            ShowToast(restoredFiles > 0 ? $"Восстановлено {restoredFiles} {FileCountWord(restoredFiles)}. {ex.Message}" : ex.Message, ToastKind.Warning);
        }
        finally
        {
            if (restored.Count > 0)
            {
                await _agent.MarkFileChangeUndoneAsync(restored);
                if (PreviewFileTab.IsChecked == true && _previewPath != null &&
                    files.Any(file => file.Undone && PathHelper.SamePath(file.Path, _previewPath))) RenderPreviewBody();
            }
        }
    }
}
