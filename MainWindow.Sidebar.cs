using System.Windows.Media.Effects;

namespace SharpAgent;

public partial class MainWindow
{
    const double DefaultSidebarWidth = 240, MinSidebarWidth = 208, MaxSidebarWidth = 480, RailWidth = 50, DockedMinWidth = 900;
    const int ChatPageSize = 30;

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
        ChatListScroll.Visibility = SidebarResize.Visibility = VisibleIf(expanded);
        DockPanel.SetDock(SearchButton, expanded ? Dock.Right : Dock.Bottom);
        // Labels hide without leaving the layout, so the icons of the rail stay exactly where they were.
        foreach (var label in new UIElement[] { NewChatLabel, TrashLabel, TrashCount, BalanceText, SettingsLabel, MoreLabel })
            label.Visibility = expanded ? Visibility.Visible : Visibility.Hidden;
        SetHint(SidebarToggle, expanded ? Tr("Скрыть список чатов (Ctrl+H)", "Hide chat list (Ctrl+H)") : Tr("Показать список чатов (Ctrl+H)", "Show chat list (Ctrl+H)"));
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
        SettingsNavColumn.Width = new(SidebarExpanded ? width : DefaultSidebarWidth);
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

    bool FlyoutShows(ContentControl card) => IsShown(FlyoutLayer) && IsShown(card);

    void ShowFlyout(ContentControl card, Button owner)
    {
        var origin = owner.TranslatePoint(new(), RootGrid);
        card.Margin = new(origin.X, 8, 8, RootGrid.ActualHeight - origin.Y + 4);
        TrashCard.Visibility = VisibleIf(card == TrashCard);
        UsageCard.Visibility = VisibleIf(card == UsageCard);
        _flyoutOwner = owner;
        FlyoutLayer.Visibility = Visibility.Visible;
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

    Task? _chatOperation;
    bool ChatFileBusy => _chatOperation != null || _attachmentImports > 0;

    bool WaitForChatFile(bool busy)
    {
        if (busy) ShowToast(Tr("Подождите: чат или вложения ещё обрабатываются", "Wait for the chat or attachments to finish processing"));
        return busy;
    }

    async Task RunChatOperationAsync(Func<Task> operation, bool showSkeleton = false)
    {
        if (WaitForChatFile(ChatFileBusy)) return;
        var task = _chatOperation = Dispatcher.InvokeAsync(operation, showSkeleton ? DispatcherPriority.Background : DispatcherPriority.Normal).Task.Unwrap();
        if (showSkeleton) SetChatLoading(true);
        UpdateSendButton();
        try
        {
            await task;
            if (showSkeleton) await Dispatcher.Yield(DispatcherPriority.Background);
        }
        finally
        {
            _chatOperation = null;
            if (showSkeleton) SetChatLoading(false);
            UpdateSendButton();
            if (_talkTasks.ContainsKey(_agent)) _ = Dispatcher.InvokeAsync(StartQueuedTalkTask);
        }
    }

    void SetChatLoading(bool loading)
    {
        ChatTranscript.Visibility = loading ? Visibility.Hidden : Visibility.Visible;
        ChatSkeleton.Visibility = VisibleIf(loading);
        UpdateComposerPlacement();
    }

    Task OpenStartupChatAsync() => RunChatOperationAsync(async () =>
    {
        var remembered = _state.CurrentChat;
        var path = await Task.Run(() =>
        {
            string? path = null;
            try { path = _agent.Store.ImportLegacy(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogError(ex); }
            if (path == null && !string.IsNullOrEmpty(remembered)) path = Path.Combine(_agent.Store.Folder, Path.GetFileName(remembered));
            return File.Exists(path) ? path : null;
        });
        if (path != null) await OpenChatAsync(path);
        else RenderCurrentChat();
        ComposerDraft = _state.Draft ?? "";
    }, showSkeleton: true);

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

    void SyncChat(AgentRunner agent, bool touched)
    {
        if (agent.ChatPath is { } path && agent.History.Count > 0)
        {
            int index = _chats.FindIndex(c => PathHelper.SamePath(c.Path, path));
            var updated = touched ? DateTime.Now : index >= 0 ? _chats[index].Updated
                : File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.Now;
            if (index >= 0) _chats.RemoveAt(index);
            _chats.Add(new(path, agent.DisplayTitle, updated));
        }
        if (_agent == agent) UpdateChatTitle();
        RenderChatList();
    }

    void UpdateChatTitle()
    {
        var text = _agent.IsTemporary ? Tr("Временный чат", "Temporary chat") :
            _agent.History.Count > 0 ? _agent.DisplayTitle.ReplaceLineEndings(" ") : "";
        ChatTitleText.Text = text.Length == 0 ? Tr("Новый чат", "New chat") : text.Length > 80 ? text[..80] + "…" : text;
        ChatTitleText.ToolTip = text.Length > 40 ? (text.Length > 400 ? text[..400] + "…" : text) : null;
        ChatMenuButton.Visibility = VisibleIf(text.Length > 0 && _agent.ChatPath != null);
        Title = text.Length == 0 ? "Sharp Agent" : ChatTitleText.Text + " — Sharp Agent";
    }

    // The title chevron opens the current chat row's own menu.
    void ChatMenu_Click(object s, RoutedEventArgs e)
    {
        if (_agent.ChatPath is not { } path || ChatRowOf(path)?.Children[0] is not Button { ContextMenu: { } menu }) return;
        menu.PlacementTarget = ChatMenuButton;
        menu.IsOpen = true;
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
                Text = Tr("Здесь появятся ваши чаты.", "Your chats will appear here."), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new(8, 12, 8, 0),
            }.Paint(ForegroundProperty, "TextMuted"));
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
                ChatList.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new(8, group == null ? 12 : 20, 8, 8) }
                    .Paint(ForegroundProperty, "TextMuted"));
                group = name;
            }
            ChatList.Children.Add(ChatRow(chat));
        }
    }

    static string GroupOf(DateTime time) => (DateTime.Today - time.Date).TotalDays switch
    {
        < 1 => Tr("Сегодня", "Today"),
        < 2 => Tr("Вчера", "Yesterday"),
        < 7 => Tr("Последние 7 дней", "Last 7 days"),
        < 30 => Tr("Последние 30 дней", "Last 30 days"),
        _ => UiLanguage.Culture.TextInfo.ToTitleCase(time.ToString(time.Year == DateTime.Today.Year ? "MMMM" : "MMMM yyyy", UiLanguage.Culture)),
    };

    Grid ChatRow(ChatSummary chat)
    {
        bool current = IsCurrent(chat.Path);
        var running = RunningChat(chat.Path) is { } runner ? _runs[runner] : null;
        bool waiting = running is { Activated: not null } or { Question: not null };
        var status = running == null ? "" : waiting ? Tr(", ждёт ответа", ", waiting for input") : Tr(", агент работает", ", agent running");
        var title = new TextBlock { Text = chat.Title, TextTrimming = TextTrimming.CharacterEllipsis };
        object content = title;
        if (running != null)
        {
            var indicator = waiting
                ? new TextBlock { Text = "●", FontSize = 9, Width = 12, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    .Paint(ForegroundProperty, "Warning")
                : MakeSpinner(12, alwaysAnimate: true);
            indicator.Margin = new(0, 0, 7, 0);
            content = new DockPanel { Children = { indicator, title } };
        }
        var row = new Grid { Tag = chat };
        var delete = FileActions.Item(Tr("Удалить в корзину", "Move to Trash"), Glyphs.Delete, () => DeleteChat(chat), "Delete");
        delete.Tag = "danger";
        var menu = new ContextMenu { Items = { FileActions.Item(Tr("Переименовать", "Rename"), Glyphs.Rename, () => BeginRename(chat, row), "F2"), delete } };
        var open = new Button
        {
            Style = Theme.Style("ChatItem"), Content = content,
            Tag = current ? "current" : null, ToolTip = chat.Title.Length > 30 || running != null ? chat.Title + status : null, ContextMenu = menu,
        };
        AutomationProperties.SetName(open, chat.Title + (current ? Tr(", открыт", ", open") : "") + status);
        open.Click += (_, _) => OpenChatFromUser(chat.Path);
        var more = Theme.IconButton(Glyphs.More, Tr("Действия с чатом", "Chat actions"), 24);
        more.Focusable = false;
        more.HorizontalAlignment = HorizontalAlignment.Right;
        more.Margin = new(0, 0, 3, 1);
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.IsOpen = true;
        };
        void ShowMore()
        {
            more.Visibility = current || row.IsMouseOver || row.IsKeyboardFocusWithin || menu.IsOpen ? Visibility.Visible : Visibility.Hidden;
            // The title gives way to the "…" button only while that button shows.
            open.Padding = new(8, 0, more.Visibility == Visibility.Visible ? 30 : 8, 0);
        }
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

    async void OpenChatFromUser(string path)
    {
        if (WaitForChatFile(ChatFileBusy)) return;
        if (!IsCurrent(path))
        {
            CloseOverlay();
            await RunChatOperationAsync(() => OpenChatAsync(path), showSkeleton: true);
        }
        else CloseOverlay();
        InputBox.Focus();
    }

    async Task OpenChatAsync(string path)
    {
        var running = RunningChat(path);
        var agent = running ?? CreateAgent();
        try
        {
            if (running == null) await agent.OpenAsync(path);
            HideBanner("chat");
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            agent.NewChat();
            if (ex is JsonException)
            {
                _chats.RemoveAll(c => PathHelper.SamePath(c.Path, path));
                string? moved = null;
                try { moved = Path.GetFileName(agent.Store.Quarantine(path)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                ShowToast(Tr("Чат повреждён и не открывается.", "This chat is damaged and cannot be opened.") + (moved != null ? Tr($" Файл отложен как {moved} в папке history.", $" The file was moved to {moved} in the history folder.") : ""), ToastKind.Error);
            }
            else ShowBanner("chat", Glyphs.Warning, Tr("Не удалось открыть чат", "Could not open chat"), ex.Message, Tr("Повторить", "Retry"), () => OpenChatFromUser(path), true);
        }
        SelectChat(agent);
    }

    void NewChat_Click(object s, RoutedEventArgs e) => StartNewChat();

    void StartNewChat()
    {
        if (SettingsOpen || WaitForChatFile(ChatFileBusy)) return;
        CloseOverlay();
        if (_agent.History.Count > 0 || _agent.ChatPath != null || !_agent.WorkMode || _agent.IsTemporary)
        {
            HideBanner("chat");
            SelectChat(CreateAgent());
        }
        InputBox.Focus();
    }

    void DeleteChat(ChatSummary chat)
    {
        if (WaitForChatFile(ChatFileBusy)) return;
        bool current = IsCurrent(chat.Path);
        if (RunningChat(chat.Path) != null)
        {
            ShowToast(Tr("Агент работает в этом чате. Откройте чат и остановите задачу перед удалением.", "The agent is running in this chat. Open it and stop the task before deleting the chat."), ToastKind.Warning);
            return;
        }
        string? trashed = null;
        try { if (File.Exists(chat.Path)) trashed = _agent.Store.MoveToTrash(chat.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(Tr("Не удалось удалить чат: ", "Could not delete chat: ") + ex.Message, ToastKind.Error);
            return;
        }
        _chats.RemoveAll(c => PathHelper.SamePath(c.Path, chat.Path));
        _chatErrors.Remove(chat.Path);
        _chatDrafts.Remove(chat.Path);
        if (current)
        {
            ComposerDraft = "";
            ClearPendingAttachments();
            SelectChat(CreateAgent());
        }
        else RenderChatList();
        UpdateTrashCount();
        if (trashed != null) ShowToast(Tr("Чат перемещён в корзину", "Chat moved to Trash"), ToastKind.Info, Tr("Вернуть", "Restore"), () => RestoreChat(trashed));
    }

    void BeginRename(ChatSummary chat, Grid row)
    {
        if (_renaming != null || WaitForChatFile(ChatFileBusy)) return;
        if (!SidebarExpanded)
        {
            // Started from the title menu: show the list first, after its own focus move.
            ToggleSidebar();
            if (SidebarExpanded)
                Dispatcher.InvokeAsync(() => { if (ChatRowOf(chat.Path) is { } shown) BeginRename(chat, shown); }, DispatcherPriority.Background);
            return;
        }
        var box = new TextBox { Style = Theme.Style("FieldTextBox"), Text = chat.Title, Height = 30, Padding = new(7, 0, 7, 0) };
        ContextMenuService.SetIsEnabled(box, false);
        AutomationProperties.SetName(box, Tr("Новое название чата", "New chat title"));
        void Finish(bool commit, bool refocus)
        {
            if (_renaming != row) return;
            _renaming = null;
            row.Children.Remove(box);
            var title = box.Text.Trim();
            if (commit && title.Length > 0 && title != chat.Title) _ = RunChatOperationAsync(() => RenameChatAsync(chat, title));
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

    Grid? ChatRowOf(string path) => ChatList.Children.OfType<Grid>().FirstOrDefault(r => r.Tag is ChatSummary c && PathHelper.SamePath(c.Path, path));

    void FocusChatRow(string path) => (ChatRowOf(path)?.Children[0] as Button)?.Focus();

    async Task RenameChatAsync(ChatSummary chat, string title)
    {
        try
        {
            if ((IsCurrent(chat.Path) ? _agent : RunningChat(chat.Path)) is { } agent) await agent.RenameAsync(title);
            else await Task.Run(() => _agent.Store.Rename(chat.Path, title));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowToast(Tr("Не удалось переименовать чат: ", "Could not rename chat: ") + ex.Message, ToastKind.Error);
            return;
        }
        int index = _chats.FindIndex(c => PathHelper.SamePath(c.Path, chat.Path));
        if (index >= 0) _chats[index] = _chats[index] with { Title = title };
        SyncChat(_agent, touched: false);
    }
}
