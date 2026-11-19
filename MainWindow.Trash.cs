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
        TrashPanel.Children.Add(TrashNote("Загружаю корзину…"));
        if (!FlyoutShows(TrashCard)) ShowFlyout(TrashCard, TrashButton);
        List<ChatSummary> chats;
        try { chats = await Task.Run(() => _agent.Store.ListTrash().OrderByDescending(c => c.Updated).ToList()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (version == _trashVersion && FlyoutShows(TrashCard))
            {
                TrashPanel.Children.Clear();
                TrashPanel.Children.Add(TrashNote("Не удалось прочитать корзину."));
            }
            return;
        }
        if (version != _trashVersion || !FlyoutShows(TrashCard)) return;
        TrashPanel.Children.Clear();
        var header = new DockPanel { Margin = new(8, 0, 4, 0), MinHeight = 28 };
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
        header.Children.Add(new TextBlock { Text = "Корзина", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        FrameworkElement caption = chats.Count > 0 ? TrashNote("Удалённые чаты хранятся здесь, пока вы не удалите их навсегда.") : new StackPanel
        {
            Margin = new(16, 20, 16, 24),
            Children =
            {
                new TextBlock { Text = Glyphs.Delete, FontFamily = Theme.Icons, FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 12) }
                    .Paint(TextBlock.ForegroundProperty, "TextDim"),
                new TextBlock { Text = "Пусто. Удалённые чаты попадают сюда, и их можно вернуть.", FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }
                    .Paint(TextBlock.ForegroundProperty, "TextDim"),
            },
        };
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

    Border TrashRow(ChatSummary chat)
    {
        var restore = Theme.IconButton(Glyphs.Undo, $"Вернуть чат «{chat.Title}»");
        restore.Click += (_, _) => RestoreChat(chat.Path);
        var delete = Theme.IconButton(Glyphs.Delete, $"Удалить чат «{chat.Title}» навсегда");
        delete.Click += (_, _) =>
        {
            if (DarkDialog.Ask(this, "Удалить чат навсегда?", "Восстановить его будет нельзя.", "Удалить навсегда", danger: true, code: chat.Title))
                RemoveFromTrash([chat]);
        };
        var actions = new StackPanel { Style = (Style)FlyoutLayer.Resources["TrashActions"], VerticalAlignment = VerticalAlignment.Center, Children = { restore, delete } };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
        texts.Children.Add(new TextBlock
        {
            Text = chat.Title, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = chat.Title.Length > 40 ? chat.Title : null,
        }.Paint(TextBlock.ForegroundProperty, "TextSoft"));
        texts.Children.Add(Caption("Удалён " + WhenText(chat.Updated)));
        DockPanel.SetDock(actions, Dock.Right);
        return new Border { Style = (Style)FlyoutLayer.Resources["TrashRow"], Child = new DockPanel { Children = { actions, texts } } };
    }

    static TextBlock TrashNote(string text)
    {
        var note = Caption(text);
        note.Margin = new(8, 4, 8, 8);
        return note;
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
