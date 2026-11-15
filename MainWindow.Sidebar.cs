using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SharpAgent.Agent;
using SharpAgent.Tools;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private const double DefaultSidebarWidth = 260, MinSidebarWidth = 208, MaxSidebarWidth = 480;
    private const double RailWidth = 52, DockedMinWidth = 900;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private List<ChatSummary> _chats = [];
    private bool _overlayOpen;
    private Grid? _renaming;
    private bool _renderPending;
    private Button? _flyoutOwner;
    private double _sidebarDragOffset;
    private double? _sidebarWidthBeforeDrag;

    private bool Narrow => ActualWidth > 0 && ActualWidth < DockedMinWidth;
    private bool SidebarExpanded => Narrow ? _overlayOpen : !_state.SidebarCollapsed;

    private void ApplySidebar()
    {
        bool expanded = SidebarExpanded, overlay = expanded && Narrow;
        UpdateSidebarWidth();
        Grid.SetColumnSpan(Sidebar, overlay ? 2 : 1);
        Sidebar.Effect = overlay ? new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black } : null;
        SidebarShade.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;

        var labels = expanded ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in new UIElement[] { Brand, NewChatLabel, TrashLabel, TrashCount, BalanceText, SettingsLabel, MoreLabel, ChatListScroll })
            element.Visibility = labels;
        ChatsButton.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        SidebarResize.Visibility = labels;
        SidebarToggle.HorizontalAlignment = expanded ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        var hint = expanded ? "Скрыть список чатов (Ctrl+H)" : "Показать список чатов (Ctrl+H)";
        SidebarToggle.ToolTip = hint;
        AutomationProperties.SetName(SidebarToggle, hint);
        UpdateLayoutWidths();
    }

    private double ClampSidebarWidth(double width)
    {
        double available = RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : Width;
        double upper = Math.Max(MinSidebarWidth, Math.Min(MaxSidebarWidth, available - (Narrow ? RailWidth : ChatMinWidth)));
        return Math.Clamp(width, MinSidebarWidth, upper);
    }

    private void UpdateSidebarWidth()
    {
        double requested = _state.SidebarWidth is { } saved && double.IsFinite(saved) ? saved : DefaultSidebarWidth;
        double width = SidebarExpanded ? ClampSidebarWidth(requested) : RailWidth;
        Sidebar.Width = width;
        SidebarColumn.Width = new GridLength(Narrow ? RailWidth : width);
    }

    private bool ResizeSidebar(double requested)
    {
        if (!SidebarExpanded) return false;
        double width = ClampSidebarWidth(requested);
        if (Math.Abs(width - Sidebar.Width) < .1) return false;
        _state.SidebarWidth = width;
        UpdateSidebarWidth();
        return true;
    }

    private void SidebarResize_DragStarted(object sender, DragStartedEventArgs e)
    {
        _sidebarWidthBeforeDrag = _state.SidebarWidth;
        _sidebarDragOffset = Sidebar.Width - Mouse.GetPosition(RootGrid).X;
    }

    private void SidebarResize_DragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeSidebar(Mouse.GetPosition(RootGrid).X + _sidebarDragOffset);

    private void SidebarResize_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (e.Canceled)
        {
            _state.SidebarWidth = _sidebarWidthBeforeDrag;
            UpdateSidebarWidth();
        }
        else if (_state.SidebarWidth != _sidebarWidthBeforeDrag)
            _state.Save();
    }

    private void SidebarResize_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right)) return;
        if (ResizeSidebar(Sidebar.Width + (e.Key == Key.Left ? -16 : 16))) _state.Save();
        e.Handled = true;
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void SidebarShade_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseOverlay();

    private void ToggleSidebar()
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        bool expand = !SidebarExpanded;
        if (Narrow) _overlayOpen = expand;
        else
        {
            _state.SidebarCollapsed = !expand;
            _state.Save();
        }
        ApplySidebar();
        if (expand) Dispatcher.InvokeAsync(FocusChatList, DispatcherPriority.Input);
    }

    private void CloseOverlay()
    {
        if (!_overlayOpen) return;
        _overlayOpen = false;
        ApplySidebar();
    }

    private bool FlyoutShows(Border card) =>
        FlyoutLayer.Visibility == Visibility.Visible && card.Visibility == Visibility.Visible;

    private void ShowFlyout(Border card, Button owner)
    {
        var origin = owner.TranslatePoint(new Point(), RootGrid);
        card.Margin = new Thickness(origin.X, 8, 8, RootGrid.ActualHeight - origin.Y + 4);
        TrashCard.Visibility = card == TrashCard ? Visibility.Visible : Visibility.Collapsed;
        UsageCard.Visibility = card == UsageCard ? Visibility.Visible : Visibility.Collapsed;
        _flyoutOwner = owner;
        FlyoutLayer.Visibility = Visibility.Visible;
        if (Theme.AnimationsEnabled)
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        if (InputManager.Current.MostRecentInputDevice is KeyboardDevice)
            Dispatcher.InvokeAsync(() => card.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
                DispatcherPriority.Input);
    }

    private void CloseFlyout()
    {
        if (FlyoutLayer.Visibility != Visibility.Visible) return;
        bool hadFocus = FlyoutLayer.IsKeyboardFocusWithin;
        FlyoutLayer.Visibility = Visibility.Collapsed;
        if (hadFocus) _flyoutOwner?.Focus();
        _flyoutOwner = null;
    }

    private void FlyoutLayer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, FlyoutLayer)) CloseFlyout();
    }

    private void FocusChatList()
    {
        var rows = ChatList.Children.OfType<Grid>().ToList();
        var row = rows.FirstOrDefault(r => r.Children[0] is Button { Tag: "current" }) ?? rows.FirstOrDefault();
        (row?.Children[0] as Button)?.Focus();
    }

    private void OpenStartupChat()
    {
        string? path = null;
        try
        {
            path = _agent.Store.ImportLegacy();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.LogError(ex);
        }
        if (path == null && !string.IsNullOrEmpty(_state.CurrentChat))
            path = Path.Combine(_agent.Store.Folder, Path.GetFileName(_state.CurrentChat));

        if (path != null && File.Exists(path)) OpenChat(path);
        else RenderCurrentChat();
    }

    private async Task LoadChatsAsync()
    {
        var store = _agent.Store;
        try
        {
            _chats = (await Task.Run(store.List)).FindAll(c => File.Exists(c.Path));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        UpdateTrashCount();
        SyncCurrentChat(touched: false);
    }

    private void SyncCurrentChat(bool touched)
    {
        if (_agent.ChatPath is { } path && _agent.History.Count > 0)
        {
            int index = _chats.FindIndex(c => PathHelper.SamePath(c.Path, path));
            var updated = touched || index < 0 ? DateTime.Now : _chats[index].Updated;
            if (index >= 0) _chats.RemoveAt(index);
            _chats.Add(new ChatSummary(path, ChatStore.TitleOf(_agent.History, _agent.Title), updated));
        }
        UpdateChatTitle();
        RenderChatList();
    }

    private void OnChatCreated()
    {
        _state.CurrentChat = Path.GetFileName(_agent.ChatPath);
        _state.Save();
        SyncCurrentChat(touched: true);
    }

    private void UpdateChatTitle()
    {
        var text = _agent.History.Count > 0 ? ChatStore.TitleOf(_agent.History, _agent.Title).ReplaceLineEndings(" ") : "";
        ChatTitleText.Text = text.Length == 0 ? "Новый чат" : text.Length > 80 ? text[..80] + "…" : text;
        ChatTitleText.ToolTip = text.Length > 40 ? (text.Length > 400 ? text[..400] + "…" : text) : null;
        Title = text.Length == 0 ? "Sharp Agent" : ChatTitleText.Text + " — Sharp Agent";
    }

    private bool IsCurrent(ChatSummary chat) => _agent.ChatPath != null && PathHelper.SamePath(chat.Path, _agent.ChatPath);

    private void RenderChatList()
    {
        if (_renaming != null)
        {
            _renderPending = true;
            return;
        }
        bool focused = ChatList.IsKeyboardFocusWithin;
        ChatList.Children.Clear();
        if (_chats.Count == 0)
        {
            ChatList.Children.Add(new TextBlock
            {
                Text = "Здесь появятся ваши чаты.", FontSize = 12, Foreground = Theme.TextMuted,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 10, 0),
            });
            return;
        }

        string? group = null;
        foreach (var chat in _chats.OrderByDescending(c => c.Updated))
        {
            var name = GroupOf(chat.Updated);
            if (name != group)
            {
                ChatList.Children.Add(new TextBlock
                {
                    Text = name, FontSize = 12, Foreground = Theme.TextMuted,
                    Margin = new Thickness(10, group == null ? 4 : 16, 10, 6),
                });
                group = name;
            }
            ChatList.Children.Add(ChatRow(chat));
        }
        if (focused) FocusChatList();
    }

    private static string GroupOf(DateTime time)
    {
        double days = (DateTime.Today - time.Date).TotalDays;
        if (days < 1) return "Сегодня";
        if (days < 2) return "Вчера";
        if (days < 7) return "Последние 7 дней";
        if (days < 30) return "Последние 30 дней";
        var month = time.ToString(time.Year == DateTime.Today.Year ? "MMMM" : "MMMM yyyy", Ru);
        return char.ToUpper(month[0], Ru) + month[1..];
    }

    private Grid ChatRow(ChatSummary chat)
    {
        bool current = IsCurrent(chat);
        var row = new Grid { Margin = new Thickness(0, 0, 0, 2), Tag = chat };
        var open = new Button
        {
            Style = Theme.Style("ChatItem"),
            Content = new TextBlock { Text = chat.Title, TextTrimming = TextTrimming.CharacterEllipsis },
            Tag = current ? "current" : null,
            ToolTip = chat.Title.Length > 30 ? chat.Title : null,
        };
        AutomationProperties.SetName(open, current ? chat.Title + ", открыт" : chat.Title);
        open.Click += (_, _) => OpenChatFromUser(chat.Path);

        var menu = new ContextMenu();
        menu.Items.Add(FileActions.Item("Переименовать", Glyphs.Rename, () => BeginRename(chat, row), "F2"));
        menu.Items.Add(FileActions.Item("Удалить в корзину", Glyphs.Delete, () => DeleteChat(chat), "Delete"));
        open.ContextMenu = menu;

        var more = Theme.IconButton(Glyphs.More, "Действия с чатом", 26, 12);
        more.Focusable = false;
        more.HorizontalAlignment = HorizontalAlignment.Right;
        more.Margin = new Thickness(0, 0, 4, 0);
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.IsOpen = true;
        };

        void ShowMore() => more.Visibility = current || row.IsMouseOver || row.IsKeyboardFocusWithin || menu.IsOpen
            ? Visibility.Visible
            : Visibility.Hidden;
        row.MouseEnter += (_, _) => ShowMore();
        row.MouseLeave += (_, _) => ShowMore();
        row.IsKeyboardFocusWithinChanged += (_, _) => ShowMore();
        menu.Closed += (_, _) => ShowMore();

        row.Children.Add(open);
        row.Children.Add(more);
        ShowMore();
        return row;
    }

    private void ChatList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not Button { Parent: Grid { Tag: ChatSummary chat } row }) return;
        if (e.Key == Key.F2) BeginRename(chat, row);
        else if (e.Key == Key.Delete) DeleteChat(chat);
        else return;
        e.Handled = true;
    }

    private void OpenChatFromUser(string path)
    {
        if (_agent.ChatPath != null && PathHelper.SamePath(path, _agent.ChatPath))
        {
            CloseOverlay();
            InputBox.Focus();
            return;
        }
        if (_cts != null)
        {
            ShowToast("Агент работает. Остановите его (Esc), чтобы открыть другой чат", ToastKind.Warning);
            return;
        }
        OpenChat(path);
        CloseOverlay();
        InputBox.Focus();
    }

    private void OpenChat(string path)
    {
        try
        {
            _agent.Open(path);
            HideBanner("chat");
        }
        catch (JsonException ex)
        {
            App.LogError(ex);
            _agent.NewChat();
            _chats.RemoveAll(c => PathHelper.SamePath(c.Path, path));
            var moved = TryQuarantine(path);
            ShowToast("Чат повреждён и не открывается." + (moved != null ? $" Файл отложен как {moved} в папке history." : ""),
                ToastKind.Error);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _agent.NewChat();
            ShowBanner("chat", Glyphs.Warning, "Не удалось открыть чат", ex.Message, "Повторить",
                () => OpenChatFromUser(path), warning: true);
        }
        _state.CurrentChat = _agent.ChatPath is { } current ? Path.GetFileName(current) : null;
        RenderCurrentChat();
    }

    private string? TryQuarantine(string path)
    {
        try
        {
            return Path.GetFileName(_agent.Store.Quarantine(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void NewChat_Click(object sender, RoutedEventArgs e) => StartNewChat();

    private void StartNewChat()
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
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

    private void DeleteChat(ChatSummary chat)
    {
        bool current = IsCurrent(chat);
        if (current && _cts != null)
        {
            ShowToast("Агент работает в этом чате. Остановите его (Esc), чтобы удалить чат", ToastKind.Warning);
            return;
        }
        string? trashed = null;
        try
        {
            if (File.Exists(chat.Path)) trashed = _agent.Store.MoveToTrash(chat.Path);
        }
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

    private void BeginRename(ChatSummary chat, Grid row)
    {
        if (_renaming != null) return;
        var box = new TextBox
        {
            Style = Theme.Style("FieldTextBox"),
            Text = chat.Title,
            Height = 34,
            FontSize = 13,
            Padding = new Thickness(6, 0, 6, 0),
        };
        ContextMenuService.SetIsEnabled(box, false);
        AutomationProperties.SetName(box, "Новое название чата");
        void Finish(bool commit, bool refocus)
        {
            if (_renaming != row) return;
            _renaming = null;
            row.Children.Remove(box);
            var title = box.Text.Trim();
            if (commit && title.Length > 0 && title != chat.Title) RenameChat(chat, title);
            else if (_renderPending) RenderChatList();
            _renderPending = false;
            if (refocus) FocusChatRow(chat.Path);
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            Finish(e.Key == Key.Enter, refocus: true);
        };
        box.LostKeyboardFocus += (_, _) => Finish(true, refocus: false);
        _renaming = row;
        row.Children.Add(box);
        box.Focus();
        box.SelectAll();
    }

    private void FocusChatRow(string path) =>
        (ChatList.Children.OfType<Grid>().FirstOrDefault(r => r.Tag is ChatSummary c && PathHelper.SamePath(c.Path, path))
            ?.Children[0] as Button)?.Focus();

    private void RenameChat(ChatSummary chat, string title)
    {
        try
        {
            if (IsCurrent(chat)) _agent.Rename(title);
            else _agent.Store.Rename(chat.Path, title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowToast("Не удалось переименовать чат: " + ex.Message, ToastKind.Error);
            return;
        }
        int index = _chats.FindIndex(c => PathHelper.SamePath(c.Path, chat.Path));
        if (index >= 0) _chats[index] = _chats[index] with { Title = title };
        SyncCurrentChat(touched: false);
    }
}
