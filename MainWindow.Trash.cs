namespace SharpAgent;

public partial class MainWindow
{
    void UpdateTrashCount()
    {
        int count = _agent.Store.TrashCount();
        TrashCount.Text = count > 0 ? count.ToString() : "";
        SetHint(TrashButton, count > 0 ? "Корзина: " + Plural(count, "удалённый чат", "удалённых чата", "удалённых чатов") : "Корзина пуста");
    }

    void Trash_Click(object s, RoutedEventArgs e)
    {
        if (FlyoutShows(TrashCard)) CloseFlyout();
        else ShowTrash();
    }

    void ShowTrash()
    {
        var chats = _agent.Store.ListTrash().OrderByDescending(c => c.Updated).ToList();
        TrashPanel.Children.Clear();
        var header = new DockPanel { Margin = new(4, 0, 4, 4) };
        if (chats.Count > 0)
        {
            var clear = new Button { Content = "Очистить", Height = 28 };
            clear.Click += (_, _) =>
            {
                if (DarkDialog.Ask(this, "Очистить корзину?",
                        $"{Plural(chats.Count, "чат будет удалён", "чата будут удалены", "чатов будут удалены")} навсегда. Восстановить их будет нельзя.",
                        "Очистить", danger: true))
                    RemoveFromTrash(chats);
            };
            DockPanel.SetDock(clear, Dock.Right);
            header.Children.Add(clear);
        }
        header.Children.Add(new TextBlock { Text = "Корзина", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
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

    DockPanel TrashRow(ChatSummary chat)
    {
        var restore = Theme.IconButton(Glyphs.Undo, $"Вернуть чат «{chat.Title}»");
        restore.Click += (_, _) => RestoreChat(chat.Path);
        var delete = Theme.IconButton(Glyphs.Delete, $"Удалить чат «{chat.Title}» навсегда");
        delete.Click += (_, _) =>
        {
            if (DarkDialog.Ask(this, "Удалить чат навсегда?", "Восстановить его будет нельзя.", "Удалить навсегда", danger: true, code: chat.Title))
                RemoveFromTrash([chat]);
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 8, 0) };
        texts.Children.Add(new TextBlock
        {
            Text = chat.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = chat.Title.Length > 40 ? chat.Title : null,
        });
        texts.Children.Add(Caption("Удалён " + WhenText(chat.Updated), 2));
        DockPanel.SetDock(delete, Dock.Right);
        DockPanel.SetDock(restore, Dock.Right);
        return new DockPanel { Margin = new(0, 0, 0, 6), Children = { delete, restore, texts } };
    }

    static string WhenText(DateTime time) => (DateTime.Today - time.Date).TotalDays switch
    {
        < 1 => "сегодня",
        < 2 => "вчера",
        _ => time.ToString(time.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy", Ru),
    } + ", " + time.ToString("HH:mm", Ru);

    void RestoreChat(string path)
    {
        try
        {
            var restored = _agent.Store.Restore(path);
            _chats.AddRange(ChatStore.Summaries([new FileInfo(restored)]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast("Не удалось вернуть чат: " + ex.Message, ToastKind.Error);
            return;
        }
        UpdateTrashCount();
        if (FlyoutShows(TrashCard)) ShowTrash();
        RenderChatList();
        ShowToast("Чат возвращён в список", ToastKind.Success);
    }

    void RemoveFromTrash(List<ChatSummary> chats)
    {
        foreach (var chat in chats)
        {
            try { _agent.Store.DeleteForever(chat.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowToast("Не удалось удалить чат: " + ex.Message, ToastKind.Error); }
        }
        UpdateTrashCount();
        ShowTrash();
    }
}
