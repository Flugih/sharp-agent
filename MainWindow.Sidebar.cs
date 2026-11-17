using System.Windows.Media.Effects;

namespace SharpAgent;

public partial class MainWindow
{
    const double DefaultSidebarWidth = 260, MinSidebarWidth = 208, MaxSidebarWidth = 480, RailWidth = 52, DockedMinWidth = 900;
    const int ChatPageSize = 30;
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    readonly List<ChatSummary> _chats = [];
    FileInfo[]? _chatFiles;
    int _nextChatFile;
    bool _overlayOpen, _renderPending, _loadingChats;
    Grid? _renaming;
    Button? _flyoutOwner;
    double _sidebarDragOffset;
    double? _sidebarWidthBeforeDrag;

    bool Narrow => ActualWidth > 0 && ActualWidth < DockedMinWidth;
    bool SidebarExpanded => Narrow ? _overlayOpen : !_state.SidebarCollapsed;

    void ApplySidebar()
    {
        bool expanded = SidebarExpanded, overlay = expanded && Narrow;
        UpdateSidebarWidth();
        Grid.SetColumnSpan(Sidebar, overlay ? 2 : 1);
        Sidebar.Effect = overlay ? new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6 } : null;
        SidebarShade.Visibility = VisibleIf(overlay);
        foreach (var element in new UIElement[] { Brand, NewChatLabel, TrashLabel, TrashCount, BalanceText, SettingsLabel, MoreLabel, ChatListScroll, SidebarResize })
            element.Visibility = VisibleIf(expanded);
        ChatsButton.Visibility = VisibleIf(!expanded);
        SidebarToggle.HorizontalAlignment = expanded ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        SetHint(SidebarToggle, expanded ? "Скрыть список чатов (Ctrl+H)" : "Показать список чатов (Ctrl+H)");
        UpdateLayoutWidths();
    }

    double ClampSidebarWidth(double width)
    {
        double available = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : Width;
        return Math.Clamp(width, MinSidebarWidth, Math.Max(MinSidebarWidth, Math.Min(MaxSidebarWidth, available - (Narrow ? RailWidth : ChatMinWidth))));
    }

    void UpdateSidebarWidth()
    {
        double width = SidebarExpanded
            ? ClampSidebarWidth(_state.SidebarWidth is { } saved && double.IsFinite(saved) ? saved : DefaultSidebarWidth)
            : RailWidth;
        Sidebar.Width = width;
        SidebarColumn.Width = new(Narrow ? RailWidth : width);
    }

    bool ResizeSidebar(double requested)
    {
        if (!SidebarExpanded) return false;
        double width = ClampSidebarWidth(requested);
        if (Math.Abs(width - Sidebar.Width) < .1) return false;
        _state.SidebarWidth = width;
        UpdateSidebarWidth();
        return true;
    }

    void SidebarResize_DragStarted(object s, DragStartedEventArgs e)
    {
        _sidebarWidthBeforeDrag = _state.SidebarWidth;
        _sidebarDragOffset = Sidebar.Width - Mouse.GetPosition(RootGrid).X;
    }

    void SidebarResize_DragDelta(object s, DragDeltaEventArgs e) => ResizeSidebar(Mouse.GetPosition(RootGrid).X + _sidebarDragOffset);

    void SidebarResize_DragCompleted(object s, DragCompletedEventArgs e)
    {
        if (e.Canceled)
        {
            _state.SidebarWidth = _sidebarWidthBeforeDrag;
            UpdateSidebarWidth();
        }
        else if (_state.SidebarWidth != _sidebarWidthBeforeDrag) _state.Save();
    }

    void SidebarResize_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        if (ResizeSidebar(Sidebar.Width + (e.Key == Key.Left ? -16 : 16))) _state.Save();
        e.Handled = true;
    }

    void SidebarToggle_Click(object s, RoutedEventArgs e) => ToggleSidebar();

    void SidebarShade_MouseLeftButtonDown(object s, MouseButtonEventArgs e) => CloseOverlay();

    void ToggleSidebar()
    {
        if (SettingsOpen) return;
        bool expand = !SidebarExpanded;
        if (Narrow) _overlayOpen = expand;
        else
        {
            _state.SidebarCollapsed = !expand;
            _state.Save();
        }
        ApplySidebar();
        if (expand)
        {
            if (AtChatListEnd()) _ = LoadChatsAsync();
            Dispatcher.InvokeAsync(FocusChatList, DispatcherPriority.Input);
        }
    }

    void CloseOverlay()
    {
        if (!_overlayOpen) return;
        _overlayOpen = false;
        ApplySidebar();
    }

    bool FlyoutShows(Border card) => IsShown(FlyoutLayer) && IsShown(card);

    void ShowFlyout(Border card, Button owner)
    {
        var origin = owner.TranslatePoint(new(), RootGrid);
        card.Margin = new(origin.X, 8, 8, RootGrid.ActualHeight - origin.Y + 4);
        TrashCard.Visibility = VisibleIf(card == TrashCard);
        UsageCard.Visibility = VisibleIf(card == UsageCard);
        _flyoutOwner = owner;
        FlyoutLayer.Visibility = Visibility.Visible;
        if (Theme.AnimationsEnabled) card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        if (InputManager.Current.MostRecentInputDevice is KeyboardDevice)
            Dispatcher.InvokeAsync(() => card.MoveFocus(new(FocusNavigationDirection.First)), DispatcherPriority.Input);
    }

    void CloseFlyout()
    {
        if (!IsShown(FlyoutLayer)) return;
        bool hadFocus = FlyoutLayer.IsKeyboardFocusWithin;
        FlyoutLayer.Visibility = Visibility.Collapsed;
        if (hadFocus) _flyoutOwner?.Focus();
        _flyoutOwner = null;
    }

    void FlyoutLayer_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, FlyoutLayer)) CloseFlyout();
    }

    void FocusChatList()
    {
        var buttons = ChatList.Children.OfType<Grid>().Select(row => row.Children[0] as Button).ToList();
        (buttons.Find(button => button?.Tag is "current") ?? buttons.FirstOrDefault())?.Focus();
    }

    void OpenStartupChat()
    {
        string? path = null;
        try { path = _agent.Store.ImportLegacy(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogError(ex); }
        if (path == null && !string.IsNullOrEmpty(_state.CurrentChat)) path = Path.Combine(_agent.Store.Folder, Path.GetFileName(_state.CurrentChat));
        if (File.Exists(path)) OpenChat(path);
        else RenderCurrentChat();
    }

    async Task LoadChatsAsync()
    {
        if (_loadingChats || _chatFiles != null && _nextChatFile >= _chatFiles.Length) return;
        _loadingChats = true;
        bool appendNext = false;
        try
        {
            var snapshot = _chatFiles;
            int start = _nextChatFile;
            var (files, next, page) = await Task.Run(() =>
            {
                var files = snapshot ?? _agent.Store.ListChatFiles();
                int next = Math.Min(start + ChatPageSize, files.Length);
                return (files, next, ChatStore.Summaries(files.Skip(start).Take(next - start)));
            });
            _chatFiles = files;
            _nextChatFile = next;
            var known = _chats.Select(chat => chat.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = page.Where(chat => File.Exists(chat.Path) && known.Add(chat.Path)).ToArray();
            _chats.AddRange(added);
            if (_renaming == null && added.Length > 0 && ChatList.Children is { Count: > 0 } children &&
                children[children.Count - 1] is Grid { Tag: ChatSummary last } && last.Updated >= added[0].Updated)
                AppendChatRows(added, GroupOf(last.Updated));
            else if (added.Length > 0 || _chats.Count == 0) RenderChatList();
            appendNext = added.Length == 0 && next < files.Length && AtChatListEnd();
        }
        catch (Exception ex) { App.LogError(ex); }
        finally { _loadingChats = false; }
        if (appendNext) _ = Dispatcher.InvokeAsync(() => LoadChatsAsync(), DispatcherPriority.Background);
    }

    bool AtChatListEnd() => ChatListScroll.IsVisible && ChatListScroll.ScrollableHeight - ChatListScroll.VerticalOffset <= 100;

    void ChatListScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_agent != null && ReferenceEquals(e.OriginalSource, sender) && AtChatListEnd()) _ = LoadChatsAsync();
    }

    void SyncCurrentChat(bool touched)
    {
        if (_agent.ChatPath is { } path && _agent.History.Count > 0)
        {
            int index = _chats.FindIndex(c => PathHelper.SamePath(c.Path, path));
            var updated = touched ? DateTime.Now : index >= 0 ? _chats[index].Updated
                : File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.Now;
            if (index >= 0) _chats.RemoveAt(index);
            _chats.Add(new(path, ChatStore.TitleOf(_agent.History, _agent.Title), updated));
        }
        UpdateChatTitle();
        RenderChatList();
    }

    void UpdateChatTitle()
    {
        var text = _agent.History.Count > 0 ? ChatStore.TitleOf(_agent.History, _agent.Title).ReplaceLineEndings(" ") : "";
        ChatTitleText.Text = text.Length == 0 ? "Новый чат" : text.Length > 80 ? text[..80] + "…" : text;
        ChatTitleText.ToolTip = text.Length > 40 ? (text.Length > 400 ? text[..400] + "…" : text) : null;
        Title = text.Length == 0 ? "Sharp Agent" : ChatTitleText.Text + " — Sharp Agent";
    }

    bool IsCurrent(string path) => _agent.ChatPath is { } current && PathHelper.SamePath(path, current);

    void RenderChatList()
    {
        _renderPending = _renaming != null;
        if (_renderPending) return;
        var focused = (Keyboard.FocusedElement as FrameworkElement)?.Parent is Grid { Tag: ChatSummary chat } ? chat.Path : null;
        ChatList.Children.Clear();
        if (_chats.Count == 0)
        {
            ChatList.Children.Add(new TextBlock
            {
                Text = "Здесь появятся ваши чаты.", FontSize = 12, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap, Margin = new(10, 4, 10, 0),
            });
            return;
        }
        AppendChatRows(_chats.OrderByDescending(c => c.Updated).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase), null);
        if (focused != null) FocusChatRow(focused);
    }

    void AppendChatRows(IEnumerable<ChatSummary> chats, string? group)
    {
        foreach (var chat in chats)
        {
            var name = GroupOf(chat.Updated);
            if (name != group)
            {
                ChatList.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = Theme.TextMuted, Margin = new(10, group == null ? 4 : 16, 10, 6) });
                group = name;
            }
            ChatList.Children.Add(ChatRow(chat));
        }
    }

    static string GroupOf(DateTime time) => (DateTime.Today - time.Date).TotalDays switch
    {
        < 1 => "Сегодня",
        < 2 => "Вчера",
        < 7 => "Последние 7 дней",
        < 30 => "Последние 30 дней",
        _ => Ru.TextInfo.ToTitleCase(time.ToString(time.Year == DateTime.Today.Year ? "MMMM" : "MMMM yyyy", Ru)),
    };

    Grid ChatRow(ChatSummary chat)
    {
        bool current = IsCurrent(chat.Path);
        var row = new Grid { Margin = new(0, 0, 0, 2), Tag = chat };
        var menu = new ContextMenu
        {
            Items =
            {
                FileActions.Item("Переименовать", Glyphs.Rename, () => BeginRename(chat, row), "F2"),
                FileActions.Item("Удалить в корзину", Glyphs.Delete, () => DeleteChat(chat), "Delete"),
            },
        };
        var open = new Button
        {
            Style = Theme.Style("ChatItem"), Content = new TextBlock { Text = chat.Title, TextTrimming = TextTrimming.CharacterEllipsis },
            Tag = current ? "current" : null, ToolTip = chat.Title.Length > 30 ? chat.Title : null, ContextMenu = menu,
        };
        AutomationProperties.SetName(open, current ? chat.Title + ", открыт" : chat.Title);
        open.Click += (_, _) => OpenChatFromUser(chat.Path);
        var more = Theme.IconButton(Glyphs.More, "Действия с чатом", 26, 12);
        more.Focusable = false;
        more.HorizontalAlignment = HorizontalAlignment.Right;
        more.Margin = new(0, 0, 4, 0);
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.IsOpen = true;
        };
        void ShowMore() => more.Visibility = current || row.IsMouseOver || row.IsKeyboardFocusWithin || menu.IsOpen ? Visibility.Visible : Visibility.Hidden;
        row.MouseEnter += (_, _) => ShowMore();
        row.MouseLeave += (_, _) => ShowMore();
        row.IsKeyboardFocusWithinChanged += (_, _) => ShowMore();
        menu.Closed += (_, _) => ShowMore();
        row.Children.Add(open);
        row.Children.Add(more);
        ShowMore();
        return row;
    }

    void ChatList_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.OriginalSource is not Button { Parent: Grid { Tag: ChatSummary chat } row }) return;
        if (e.Key == Key.F2) BeginRename(chat, row);
        else if (e.Key == Key.Delete) DeleteChat(chat);
        else return;
        e.Handled = true;
    }

    void OpenChatFromUser(string path)
    {
        if (!IsCurrent(path))
        {
            if (_cts != null)
            {
                ShowToast("Агент работает. Остановите его (Esc), чтобы открыть другой чат", ToastKind.Warning);
                return;
            }
            OpenChat(path);
        }
        CloseOverlay();
        InputBox.Focus();
    }

    void OpenChat(string path)
    {
        try
        {
            _agent.Open(path);
            HideBanner("chat");
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _agent.NewChat();
            if (ex is JsonException)
            {
                _chats.RemoveAll(c => PathHelper.SamePath(c.Path, path));
                string? moved = null;
                try { moved = Path.GetFileName(_agent.Store.Quarantine(path)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                ShowToast("Чат повреждён и не открывается." + (moved != null ? $" Файл отложен как {moved} в папке history." : ""), ToastKind.Error);
            }
            else ShowBanner("chat", Glyphs.Warning, "Не удалось открыть чат", ex.Message, "Повторить", () => OpenChatFromUser(path), true);
        }
        _state.CurrentChat = Path.GetFileName(_agent.ChatPath);
        RenderCurrentChat();
    }

    void NewChat_Click(object s, RoutedEventArgs e) => StartNewChat();

    void StartNewChat()
    {
        if (SettingsOpen) return;
        if (_cts != null)
        {
            ShowToast("Агент работает. Остановите его (Esc), чтобы начать новый чат", ToastKind.Warning);
            return;
        }
        CloseOverlay();
        if (_agent.History.Count > 0 || _agent.ChatPath != null)
        {
            _agent.NewChat();
            HideBanner("chat");
            _state.CurrentChat = null;
            RenderCurrentChat();
        }
        InputBox.Focus();
    }

    void DeleteChat(ChatSummary chat)
    {
        bool current = IsCurrent(chat.Path);
        if (current && _cts != null)
        {
            ShowToast("Агент работает в этом чате. Остановите его (Esc), чтобы удалить чат", ToastKind.Warning);
            return;
        }
        string? trashed = null;
        try { if (File.Exists(chat.Path)) trashed = _agent.Store.MoveToTrash(chat.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast("Не удалось удалить чат: " + ex.Message, ToastKind.Error);
            return;
        }
        _chats.RemoveAll(c => PathHelper.SamePath(c.Path, chat.Path));
        if (current)
        {
            _agent.NewChat();
            _state.CurrentChat = null;
            RenderCurrentChat();
        }
        else RenderChatList();
        UpdateTrashCount();
        if (trashed != null) ShowToast("Чат перемещён в корзину", ToastKind.Info, "Вернуть", () => RestoreChat(trashed));
    }

    void BeginRename(ChatSummary chat, Grid row)
    {
        if (_renaming != null) return;
        var box = new TextBox { Style = Theme.Style("FieldTextBox"), Text = chat.Title, Height = 34, FontSize = 13, Padding = new(6, 0, 6, 0) };
        ContextMenuService.SetIsEnabled(box, false);
        AutomationProperties.SetName(box, "Новое название чата");
        void Finish(bool commit, bool refocus)
        {
            if (_renaming != row) return;
            _renaming = null;
            row.Children.Remove(box);
            var title = box.Text.Trim();
            if (commit && title.Length > 0 && title != chat.Title) RenameChat(chat, title);
            if (_renderPending) RenderChatList();
            if (refocus) FocusChatRow(chat.Path);
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            Finish(e.Key == Key.Enter, true);
        };
        box.LostKeyboardFocus += (_, _) => Finish(true, false);
        _renaming = row;
        row.Children.Add(box);
        box.Focus();
        box.SelectAll();
    }

    void FocusChatRow(string path) =>
        (ChatList.Children.OfType<Grid>().FirstOrDefault(r => r.Tag is ChatSummary c && PathHelper.SamePath(c.Path, path))?.Children[0] as Button)?.Focus();

    void RenameChat(ChatSummary chat, string title)
    {
        try
        {
            if (IsCurrent(chat.Path)) _agent.Rename(title);
            else _agent.Store.Rename(chat.Path, title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowToast("Не удалось переименовать чат: " + ex.Message, ToastKind.Error);
            return;
        }
        int index = _chats.FindIndex(c => PathHelper.SamePath(c.Path, chat.Path));
        if (index >= 0) _chats[index] = _chats[index] with { Title = title };
        SyncCurrentChat(false);
    }
}
