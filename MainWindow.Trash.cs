namespace SharpAgent;

public partial class MainWindow
{
    int _trashVersion, _trashCountVersion;

    async void UpdateTrashCount()
    {
        int version = ++_trashCountVersion;
        int count;
        try { count = await Task.Run(_agent.Store.TrashCount); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (version != _trashCountVersion) return;
        TrashCount.Text = count > 0 ? count.ToString() : "";
        SetHint(TrashButton, count > 0 ? "Корзина: " + Plural(count, "удалённый чат", "удалённых чата", "удалённых чатов") : "Корзина пуста");
    }

    void Trash_Click(object s, RoutedEventArgs e)
    {
        if (FlyoutShows(TrashCard)) CloseFlyout();
        else ShowTrash();
    }

    async void ShowTrash()
    {
        int version = ++_trashVersion;
        TrashPanel.Children.Clear();
        TrashPanel.Children.Add(Caption("Загружаю корзину…", 0));
        if (!FlyoutShows(TrashCard)) ShowFlyout(TrashCard, TrashButton);
        List<ChatSummary> chats;
        try { chats = await Task.Run(() => _agent.Store.ListTrash().OrderByDescending(c => c.Updated).ToList()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (version == _trashVersion && FlyoutShows(TrashCard))
            {
                TrashPanel.Children.Clear();
                TrashPanel.Children.Add(Caption("Не удалось прочитать корзину.", 0));
            }
            return;
        }
        if (version != _trashVersion || !FlyoutShows(TrashCard)) return;
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
            int next = 0;
            void AppendPage()
            {
                int end = Math.Min(next + 30, chats.Count);
                while (next < end) list.Children.Add(TrashRow(chats[next++]));
            }
            AppendPage();
            var scroll = new ScrollViewer
            {
                Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            };
            scroll.ScrollChanged += (_, _) =>
            {
                if (scroll.ScrollableHeight - scroll.VerticalOffset < 80 && next < chats.Count) AppendPage();
            };
            TrashPanel.Children.Add(scroll);
        }
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

    async void RemoveFromTrash(List<ChatSummary> chats)
    {
        var store = _agent.Store;
        var error = await Task.Run(() =>
        {
            string? last = null;
            foreach (var chat in chats)
            {
                try { store.DeleteForever(chat.Path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { last = ex.Message; }
            }
            return last;
        });
        if (error != null) ShowToast("Не удалось удалить чат: " + error, ToastKind.Error);
        UpdateTrashCount();
        ShowTrash();
    }
}
