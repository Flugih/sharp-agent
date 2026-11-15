using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SharpAgent.Agent;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private void UpdateTrashCount()
    {
        int count = _agent.Store.TrashCount();
        TrashCount.Text = count > 0 ? count.ToString() : "";
        var hint = count > 0 ? "Корзина: " + Plural(count, "удалённый чат", "удалённых чата", "удалённых чатов") : "Корзина пуста";
        TrashButton.ToolTip = hint;
        AutomationProperties.SetName(TrashButton, hint);
    }

    private void Trash_Click(object sender, RoutedEventArgs e)
    {
        if (FlyoutShows(TrashCard)) CloseFlyout();
        else ShowTrash();
    }

    private void ShowTrash()
    {
        var chats = _agent.Store.ListTrash().OrderByDescending(c => c.Updated).ToList();
        TrashPanel.Children.Clear();

        var header = new DockPanel { Margin = new Thickness(4, 0, 4, 4) };
        if (chats.Count > 0)
        {
            var clear = new Button { Content = "Очистить", Height = 28 };
            clear.Click += (_, _) => EmptyTrash(chats);
            DockPanel.SetDock(clear, Dock.Right);
            header.Children.Add(clear);
        }
        header.Children.Add(new TextBlock
        {
            Text = "Корзина", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        var caption = Caption(chats.Count == 0
            ? "Пусто. Удалённые чаты попадают сюда, и их можно вернуть."
            : "Удалённые чаты хранятся здесь, пока вы не удалите их навсегда.", 0, 8);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(caption, Dock.Top);
        TrashPanel.Children.Add(header);
        TrashPanel.Children.Add(caption);

        if (chats.Count > 0)
        {
            var list = new StackPanel();
            foreach (var chat in chats) list.Children.Add(TrashRow(chat));
            TrashPanel.Children.Add(new ScrollViewer
            {
                Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            });
        }
        if (!FlyoutShows(TrashCard)) ShowFlyout(TrashCard, TrashButton);
    }

    private FrameworkElement TrashRow(ChatSummary chat)
    {
        var restore = Theme.IconButton(Glyphs.Undo, $"Вернуть чат «{chat.Title}»");
        restore.Click += (_, _) => RestoreChat(chat.Path);
        var delete = Theme.IconButton(Glyphs.Delete, $"Удалить чат «{chat.Title}» навсегда");
        delete.Click += (_, _) => DeleteForever(chat);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        texts.Children.Add(new TextBlock
        {
            Text = chat.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = chat.Title.Length > 40 ? chat.Title : null,
        });
        texts.Children.Add(Caption("Удалён " + WhenText(chat.Updated), 2));

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(delete, Dock.Right);
        DockPanel.SetDock(restore, Dock.Right);
        row.Children.Add(delete);
        row.Children.Add(restore);
        row.Children.Add(texts);
        return row;
    }

    private static string WhenText(DateTime time)
    {
        var day = (DateTime.Today - time.Date).TotalDays switch
        {
            < 1 => "сегодня",
            < 2 => "вчера",
            _ => time.ToString(time.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy", Ru),
        };
        return $"{day}, {time.ToString("HH:mm", Ru)}";
    }

    private void RestoreChat(string path)
    {
        try
        {
            _agent.Store.Restore(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast("Не удалось вернуть чат: " + ex.Message, ToastKind.Error);
            return;
        }
        UpdateTrashCount();
        if (FlyoutShows(TrashCard)) ShowTrash();
        _ = LoadChatsAsync();
        ShowToast("Чат возвращён в список", ToastKind.Success);
    }

    private void DeleteForever(ChatSummary chat)
    {
        if (!DarkDialog.Ask(this, "Удалить чат навсегда?", "Восстановить его будет нельзя.", "Удалить навсегда",
                danger: true, code: chat.Title))
            return;
        RemoveFromTrash([chat]);
    }

    private void EmptyTrash(List<ChatSummary> chats)
    {
        if (!DarkDialog.Ask(this, "Очистить корзину?",
                $"{Plural(chats.Count, "чат будет удалён", "чата будут удалены", "чатов будут удалены")} навсегда. Восстановить их будет нельзя.",
                "Очистить", danger: true))
            return;
        RemoveFromTrash(chats);
    }

    private void RemoveFromTrash(IEnumerable<ChatSummary> chats)
    {
        foreach (var chat in chats)
        {
            try
            {
                _agent.Store.DeleteForever(chat.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowToast("Не удалось удалить чат: " + ex.Message, ToastKind.Error);
            }
        }
        UpdateTrashCount();
        ShowTrash();
    }
}
