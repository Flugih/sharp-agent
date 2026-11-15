using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SharpAgent.Tools;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private Border ChangesCard(ProducedFile[] files)
    {
        var card = new Border
        {
            Background = Theme.Surface, BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
        };
        var list = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(12, 10, 12, 10) };
        var icon = new Border
        {
            Width = 40, Height = 40, Background = Theme.Get("SidebarBg"), CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = Glyphs.Edit, FontFamily = Theme.Icons, FontSize = 17, Foreground = Theme.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Right);
        var undo = ChangeAction("Отменить", "Отменить изменения в этой группе");
        var undoLabel = new StackPanel { Orientation = Orientation.Horizontal };
        undoLabel.Children.Add(new TextBlock { Text = "Отменить" });
        undoLabel.Children.Add(new TextBlock
        {
            Text = Glyphs.Undo, FontFamily = Theme.Icons, FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0),
        });
        undo.Content = undoLabel;
        undo.Background = Brushes.Transparent;
        undo.BorderBrush = Brushes.Transparent;
        var review = ChangeAction("Просмотреть изменения", "Открыть список изменений справа");
        buttons.Children.Add(undo);
        buttons.Children.Add(review);
        header.Children.Add(buttons);
        var heading = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        caption.Children.Add(heading);
        var totals = DiffSummary(files.Sum(file => (long?)file.Diff?.Added ?? 0), files.Sum(file => (long?)file.Diff?.Removed ?? 0));
        totals.Margin = new Thickness(0, 3, 0, 0);
        totals.HorizontalAlignment = HorizontalAlignment.Left;
        if (files.Any(file => file.Diff != null)) caption.Children.Add(totals);
        header.Children.Add(caption);
        list.Children.Add(header);

        var rows = new StackPanel { Background = Theme.Bg };
        const int shown = 3;
        var extra = new StackPanel();
        var reveal = new RevealPanel { Child = extra };
        var rowButtons = new List<Button>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            var file = files[i];
            int index = i;
            var row = new DockPanel { LastChildFill = true };
            if (file.Diff != null)
            {
                var count = DiffSummary(file.Diff);
                DockPanel.SetDock(count, Dock.Right);
                row.Children.Add(count);
            }
            row.Children.Add(new TextBlock
            {
                Text = CardTitle(file.Path, keepExtension: true), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var button = new Button
            {
                Content = row, Style = Theme.Style("RowButton"), Height = 36, FontSize = 13,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = file.Path,
                ContextMenu = FileActions.BuildMenu(this, file.Path),
            };
            AutomationProperties.SetName(button, $"Просмотреть {CardTitle(file.Path, keepExtension: true)}");
            button.Click += (_, _) => ReviewChanges(files, index);
            rowButtons.Add(button);
            (i < shown ? rows : extra).Children.Add(button);
        }
        rows.Children.Add(reveal);
        list.Children.Add(new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = rows });
        if (files.Length > shown)
        {
            int remaining = files.Length - shown;
            string expand = $"Показать ещё {remaining} {FileCountWord(remaining)}";
            var toggle = new Button { Style = Theme.Style("RowButton"), Height = 35, FontSize = 13 };
            void UpdateToggle()
            {
                bool opened = reveal.IsOpen;
                string label = opened ? "Свернуть список" : expand;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Text = label });
                content.Children.Add(new TextBlock
                {
                    Text = Glyphs.ChevronDown, FontFamily = Theme.Icons, FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                    RenderTransformOrigin = new Point(.5, .5), RenderTransform = opened ? new RotateTransform(180) : Transform.Identity,
                });
                toggle.Content = content;
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
            heading.Text = remaining == 0 ? "Изменения отменены" :
                remaining < files.Length ? $"Осталось {remaining} {FileCountWord(remaining)}" :
                $"Изменено {files.Length} {FileCountWord(files.Length)}";
            totals.Visibility = remaining == files.Length ? Visibility.Visible : Visibility.Collapsed;
            undo.Visibility = remaining == 0 ? Visibility.Collapsed : Visibility.Visible;
            undo.IsEnabled = files.Where(file => !file.Undone)
                .All(file => file.Change is { Created: true } or { Backup: not null });
            undo.ToolTip = undo.IsEnabled ? "Отменить изменения в этой группе" : "Для части файлов нет резервной копии";
            ToolTipService.SetShowOnDisabled(undo, true);
            for (int i = 0; i < files.Length; i++)
                rowButtons[i].Opacity = files[i].Undone ? .55 : 1;
        }
        undo.Click += (_, _) => { UndoChanges(files); Refresh(); };
        review.Click += (_, _) => ReviewChanges(files, Math.Max(0, Array.FindIndex(files, file => file.Diff != null)));
        Refresh();
        card.Child = list;
        return card;
    }

    private static string FileCountWord(int count) => (count % 100) is >= 11 and <= 14 ? "файлов" : (count % 10) switch
    {
        1 => "файл",
        2 or 3 or 4 => "файла",
        _ => "файлов",
    };

    private static Button ChangeAction(string text, string hint)
    {
        var button = new Button { Content = text, ToolTip = hint, Height = 28, FontSize = 12, Padding = new Thickness(8, 0, 8, 0) };
        AutomationProperties.SetName(button, hint);
        return button;
    }

    private void ReviewChanges(ProducedFile[] files, int selected)
    {
        var file = files[selected];
        ShowPreview(file.Path, file.Diff, review: true);
        var header = new TextBlock
        {
            Text = $"{selected + 1}/{files.Length} · {CardTitle(file.Path, keepExtension: true)}",
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        PreviewChangePicker.Content = header;
        var picker = new ContextMenu { PlacementTarget = PreviewChangePicker, Placement = PlacementMode.Bottom, MaxHeight = 320 };
        for (int i = 0; i < files.Length; i++)
        {
            int index = i;
            var title = CardTitle(files[i].Path, keepExtension: true).Replace("_", "__");
            var item = FileActions.Item(title, i == selected ? Glyphs.Check : null, () => ReviewChanges(files, index));
            item.ToolTip = files[i].Path;
            picker.Items.Add(item);
        }
        PreviewChangePicker.ContextMenu = picker;
    }

    private void PreviewChangePicker_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewChangePicker.ContextMenu is { } menu) menu.IsOpen = true;
    }

    private void UndoChanges(ProducedFile[] files)
    {
        if (_cts != null)
        {
            ShowToast("Дождитесь завершения текущего запроса, чтобы отменить изменения.", ToastKind.Warning);
            return;
        }
        var pending = files.Where(file => !file.Undone).ToArray();
        if (pending.Length == 0 || pending.Any(file => file.Change == null)) return;
        if (!DarkDialog.Ask(this, "Отменить изменения?",
                "Прежние версии файлов будут восстановлены. Новые файлы переместятся в Корзину.",
                "Отменить изменения", "Оставить", danger: true))
            return;
        var restored = new List<string>();
        int restoredFiles = 0;
        try
        {
            foreach (var file in pending) FileChanges.EnsureRestorable(_toolContext, file.Change!);
            foreach (var file in pending)
            {
                FileChanges.Restore(_toolContext, file.Change!);
                int index = Array.IndexOf(files, file);
                files[index] = file with { Undone = true };
                restored.AddRange(file.Ids);
                restoredFiles++;
            }
            ShowToast("Изменения отменены", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException)
        {
            ShowToast(restoredFiles > 0 ? $"Восстановлено {restoredFiles} {FileCountWord(restoredFiles)}. {ex.Message}" : ex.Message,
                ToastKind.Warning);
        }
        finally
        {
            if (restored.Count > 0)
            {
                _agent.MarkFileChangeUndone(restored);
                if (PreviewFileTab.IsChecked == true && _previewPath != null &&
                    files.Any(file => file.Undone && PathHelper.SamePath(file.Path, _previewPath))) RenderPreviewBody();
            }
        }
    }
}
