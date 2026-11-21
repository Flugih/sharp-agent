namespace SharpAgent;

public partial class MainWindow
{
    const int HistoryPageSize = 8, HistoryPageLimit = 3;
    const double HistoryEdge = 110;
    readonly LinkedList<(int Start, int End, StackPanel View)> _historyPages = new();
    int _tailStart, _historyVersion;
    ChatHistory.View _renderedHistory = new ChatHistory().Snapshot();
    bool _changingHistory, _historyPagePending;
    DispatcherTimer? _chatMotion;

    void ResetHistoryPages()
    {
        StopChatMotion();
        int version = ++_historyVersion;
        _historyPagePending = false;
        _changingHistory = true;
        try
        {
            ChatPages.Children.RemoveRange(0, ChatPages.Children.Count - 1);
            _historyPages.Clear();
            ChatPanel.Visibility = Visibility.Visible;
            _renderedHistory = CurrentRun?.History ?? _agent.HistoryView;
            _tailStart = FindHistoryPageStart(_renderedHistory.Count);
            RenderHistory(_renderedHistory.Range(_tailStart, _renderedHistory.Count).ToArray(), _tailStart);
        }
        finally { _changingHistory = false; }
        Dispatcher.InvokeAsync(() =>
        {
            if (version != _historyVersion || !_autoScroll) return;
            ChatScroll.ScrollToEnd();
            UpdateScrollDownButton();
        }, DispatcherPriority.Loaded);
    }

    int FindHistoryPageStart(int end)
    {
        int remaining = HistoryPageSize;
        for (int i = end - 1; i >= 0; i--)
            if (_renderedHistory.IsUser(i) && --remaining == 0)
                return i;
        return 0;
    }

    int FindHistoryPageEnd(int start)
    {
        int remaining = HistoryPageSize;
        for (int i = start + 1; i < _tailStart; i++)
            if (_renderedHistory.IsUser(i) && --remaining == 0) return i;
        return _tailStart;
    }

    bool NearHistoryEdge(bool older) => older
        ? (_historyPages.First?.Value.Start ?? _tailStart) > 0 && ChatScroll.VerticalOffset <= HistoryEdge
        : !IsShown(ChatPanel) && ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset <= HistoryEdge;

    void CheckHistory(bool older)
    {
        if (_changingHistory || _historyPagePending || ChatScroll.IsMouseCaptureWithin || !NearHistoryEdge(older)) return;
        _historyPagePending = true;
        int version = _historyVersion;
        Dispatcher.InvokeAsync(() =>
        {
            if (version != _historyVersion) return;
            _historyPagePending = false;
            if (_chatMotion == null && !ChatScroll.IsMouseCaptureWithin && NearHistoryEdge(older)) LoadHistoryPage(older);
        }, DispatcherPriority.Background);
    }

    void LoadHistoryPage(bool older)
    {
        _changingHistory = true;
        _autoScroll = false;
        try
        {
            int boundary = older ? _historyPages.First?.Value.Start ?? _tailStart : _historyPages.Last!.Value.End;
            int start = older ? FindHistoryPageStart(boundary) : boundary;
            int end = older ? boundary : FindHistoryPageEnd(boundary);
            var page = RenderHistoryPage(start, end);
            double offset = ChatScroll.VerticalOffset;
            if (older)
            {
                _historyPages.AddFirst((start, end, page));
                ChatPages.Children.Insert(0, page);
            }
            else
            {
                _historyPages.AddLast((start, end, page));
                ChatPages.Children.Insert(ChatPages.Children.Count - 1, page);
            }
            if (_historyPages.Count > HistoryPageLimit)
            {
                var remove = (older ? _historyPages.Last : _historyPages.First)!;
                if (!older) offset -= remove.Value.View.ActualHeight;
                ChatPages.Children.Remove(remove.Value.View);
                _historyPages.Remove(remove);
            }
            if (IsShown(ChatPanel) != (_historyPages.Last!.Value.End == _tailStart))
            {
                ReleaseStickyProgress();
                ChatPanel.Visibility = VisibleIf(_historyPages.Last.Value.End == _tailStart);
                UpdateChangesPill();
            }
            ChatScroll.UpdateLayout();
            ChatScroll.ScrollToVerticalOffset(Math.Max(0, offset + (older ? page.ActualHeight : 0)));
            ChatScroll.UpdateLayout();
        }
        finally { _changingHistory = false; }
        UpdateScrollDownButton();
    }

    StackPanel RenderHistoryPage(int start, int end)
    {
        var page = new StackPanel();
        FlushModelProgress();
        var live = _chatView;
        _chatView = new(page);
        try { RenderHistory(_renderedHistory.Range(start, end).ToArray(), start); }
        finally { _chatView = live; }
        return page;
    }

    void ScrollChatBy(double delta)
    {
        StopChatMotion();
        _autoScroll = IsShown(ChatPanel) && delta > 0 && ChatScroll.VerticalOffset + delta >= ChatScroll.ScrollableHeight;
        ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset + delta);
        if (delta != 0) CheckHistory(older: delta < 0);
    }

    void ScrollToLatest()
    {
        StopChatMotion();
        if (!IsShown(ChatPanel))
        {
            _historyVersion++;
            _historyPagePending = false;
            _historyPages.Clear();
            ChatPages.Children.RemoveRange(0, ChatPages.Children.Count - 1);
            ChatPanel.Visibility = Visibility.Visible;
            UpdateChangesPill();
            _autoScroll = true;
            ChatScroll.ScrollToEnd();
            return;
        }
        var start = ChatScroll.VerticalOffset;
        if (!Theme.AnimationsEnabled || ChatScroll.ScrollableHeight - start < 1)
        {
            _autoScroll = true;
            ChatScroll.ScrollToEnd();
            return;
        }
        _autoScroll = false;
        var clock = Stopwatch.StartNew();
        var timer = _chatMotion = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double ratio = Math.Min(1, clock.Elapsed.TotalMilliseconds / 340);
            ChatScroll.ScrollToVerticalOffset(start + (ChatScroll.ScrollableHeight - start) * (1 - Math.Pow(1 - ratio, 3)));
            if (ratio < 1) return;
            StopChatMotion();
            _autoScroll = true;
            UpdateScrollDownButton();
        };
        timer.Start();
    }

    void StopChatMotion()
    {
        _chatMotion?.Stop();
        _chatMotion = null;
    }

    void UpdateScrollDownButton() => ScrollDownButton.Visibility = VisibleIf(!IsShown(ChatPanel) || ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset > 80);

    void ChatScroll_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node != null && node != ChatScroll; node = ParentOf(node))
            if (node is ScrollBar or CodeBlockView or TextBoxBase)
            {
                StopChatMotion();
                _autoScroll = false;
                return;
            }
    }
}
