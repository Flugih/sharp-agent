using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SharpAgent.Core;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private const int HistoryPageSize = 8;
    private const double HistoryEdge = 110;
    private int _historyStart, _historyVersion;
    private bool _changingHistory, _historyPagePending;
    private DispatcherTimer? _chatMotion;

    private void ResetHistoryPages()
    {
        StopChatMotion();
        int version = ++_historyVersion;
        _historyPagePending = false;
        _changingHistory = true;
        try
        {
            ChatPages.Children.RemoveRange(0, ChatPages.Children.Count - 1);
            _historyStart = FindHistoryPageStart(_agent.History.Count);
            RenderHistory(_agent.History.Skip(_historyStart).ToArray());
        }
        finally
        {
            _changingHistory = false;
        }
        Dispatcher.InvokeAsync(() =>
        {
            if (version != _historyVersion || !_autoScroll) return;
            ChatScroll.ScrollToEnd();
            UpdateScrollDownButton();
        }, DispatcherPriority.Loaded);
    }

    private int FindHistoryPageStart(int end)
    {
        int remaining = HistoryPageSize;
        for (int i = end - 1; i >= 0; i--)
            if (_agent.History[i] is JsonObject message && JsonUtil.Str(message["role"]) == "user" && --remaining == 0)
                return i;
        return 0;
    }

    private void CheckOlderHistory()
    {
        if (_changingHistory || _historyPagePending || _historyStart == 0 || ChatScroll.VerticalOffset > HistoryEdge) return;
        _historyPagePending = true;
        int version = _historyVersion;
        Dispatcher.InvokeAsync(() =>
        {
            if (version != _historyVersion) return;
            _historyPagePending = false;
            if (_historyStart != 0 && _chatMotion == null && ChatScroll.VerticalOffset <= HistoryEdge) LoadOlderHistory();
        }, DispatcherPriority.Background);
    }

    private void LoadOlderHistory()
    {
        _changingHistory = true;
        _autoScroll = false;
        try
        {
            int start = FindHistoryPageStart(_historyStart);
            var page = new StackPanel();
            var live = _chatView;
            _chatView = new ChatRenderState(page);
            try { RenderHistory(_agent.History.Skip(start).Take(_historyStart - start).ToArray()); }
            finally { _chatView = live; }
            _historyStart = start;
            double offset = ChatScroll.VerticalOffset;
            ChatPages.Children.Insert(0, page);
            ChatScroll.UpdateLayout();
            ChatScroll.ScrollToVerticalOffset(offset + page.ActualHeight);
            ChatScroll.UpdateLayout();
        }
        finally
        {
            _changingHistory = false;
        }
        UpdateScrollDownButton();
    }

    private void ScrollChatBy(double delta)
    {
        StopChatMotion();
        _autoScroll = delta > 0 && ChatScroll.VerticalOffset + delta >= ChatScroll.ScrollableHeight;
        ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset + delta);
        if (delta < 0) CheckOlderHistory();
    }

    private void ScrollToLatest()
    {
        StopChatMotion();
        var start = ChatScroll.VerticalOffset;
        if (!Theme.AnimationsEnabled || ChatScroll.ScrollableHeight - start < 1)
        {
            _autoScroll = true;
            ChatScroll.ScrollToEnd();
            return;
        }
        _autoScroll = false;
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double ratio = Math.Min(1, clock.Elapsed.TotalMilliseconds / 340);
            ChatScroll.ScrollToVerticalOffset(start + (ChatScroll.ScrollableHeight - start) * (1 - Math.Pow(1 - ratio, 3)));
            if (ratio < 1) return;
            StopChatMotion();
            _autoScroll = true;
            UpdateScrollDownButton();
        };
        _chatMotion = timer;
        timer.Start();
    }

    private void StopChatMotion()
    {
        _chatMotion?.Stop();
        _chatMotion = null;
    }

    private void UpdateScrollDownButton() =>
        ScrollDownButton.Visibility = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset > 80
            ? Visibility.Visible : Visibility.Collapsed;

    private void ChatScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node != null && node != ChatScroll; node = ParentOf(node))
            if (node is ScrollBar or CodeBlockView)
            {
                StopChatMotion();
                _autoScroll = false;
                return;
            }
    }
}
