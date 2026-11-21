using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media.Imaging;

namespace SharpAgent;

public partial class MainWindow
{
    static readonly DependencyProperty SearchMessageProperty = DependencyProperty.RegisterAttached("SearchMessage", typeof(int), typeof(MainWindow), new PropertyMetadata(-1));
    CancellationTokenSource? _searchCancellation;
    SearchArea _searchArea;
    IInputElement? _searchFocus;

    void Search_Click(object sender, RoutedEventArgs e) => OpenSearch();

    void OpenSearch()
    {
        if (SettingsOpen || _agent == null || WaitForChatFile(ChatFileBusy)) return;
        if (IsShown(SearchLayer)) { SearchBox.Focus(); return; }
        CloseFlyout();
        CloseOverlay();
        _searchFocus = Keyboard.FocusedElement;
        double scale = Math.Min(1, 1400 / Math.Max(1, RootGrid.ActualWidth));
        int width = Math.Max(1, (int)(RootGrid.ActualWidth * scale)), height = Math.Max(1, (int)(RootGrid.ActualHeight * scale));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Theme.Get("Bg"), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(RootGrid), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        SearchBlur.Source = bitmap;
        _searchArea = SearchArea.All;
        SearchBox.Clear();
        SearchFilters.Children.Clear();
        (SearchArea Area, string Label)[] filters =
        [
            (SearchArea.All, Tr("Все", "All")), (SearchArea.Actions, Tr("Действия", "Actions")),
            (SearchArea.Chats, Tr("Чаты", "Chats")), (SearchArea.Folders, Tr("Папки", "Folders")),
            (SearchArea.Files, Tr("Файлы", "Files")), (SearchArea.Text, Tr("Текст", "Text")),
        ];
        foreach (var (area, label) in filters)
        {
            var tab = new RadioButton
            {
                Content = label, Tag = area, GroupName = "SearchArea", IsChecked = area == SearchArea.All,
                Style = Theme.Style("TabToggle"), Cursor = Cursors.Hand, Margin = new(0, 0, 4, 0),
            };
            tab.Checked += (_, _) => { _searchArea = area; QueueSearch(immediate: true); };
            SearchFilters.Children.Add(tab);
        }
        SearchLayer.Visibility = Visibility.Visible;
        SearchBox.Focus();
        QueueSearch(immediate: true);
    }

    void CloseSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        SearchLayer.Visibility = Visibility.Collapsed;
        SearchBlur.Source = null;
        SearchResults.ItemsSource = null;
        if (_searchFocus is UIElement { IsVisible: true } element) element.Focus();
        else InputBox.Focus();
        _searchFocus = null;
    }

    void SearchClose_Click(object sender, RoutedEventArgs e) => CloseSearch();

    void SearchBackdrop_Click(object sender, MouseButtonEventArgs e) => CloseSearch();

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchLayer == null || !IsShown(SearchLayer)) return;
        QueueSearch();
    }

    async void QueueSearch(bool immediate = false)
    {
        if (!IsShown(SearchLayer)) return;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        var cancellation = _searchCancellation = new();
        var ct = cancellation.Token;
        var query = SearchBox.Text.Trim();
        var area = _searchArea;
        var context = new ToolContext
        {
            WorkingDirectory = _toolContext.WorkingDirectory, AppFolder = _toolContext.AppFolder,
            InstallationFolder = _toolContext.InstallationFolder, SandboxRoot = _toolContext.SandboxRoot,
        };
        var store = _agent.Store;
        SearchHint.Visibility = VisibleIf(SearchBox.Text.Length == 0);
        SearchStatus.Text = Tr("Ищу…", "Searching…");
        SearchStatus.Visibility = Visibility.Visible;
        SearchLimitHint.Visibility = Visibility.Collapsed;
        SearchResults.ItemsSource = null;
        try
        {
            if (!immediate) await Task.Delay(220, ct);
            var (matches, limited) = await Task.Run(() => AppSearch.Find(context, store, query, area, ct), ct);
            if (ct.IsCancellationRequested || !IsShown(SearchLayer)) return;
            var view = CollectionViewSource.GetDefaultView(matches);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SearchMatch.Group)));
            SearchResults.ItemsSource = view;
            SearchResults.SelectedIndex = matches.Count > 0 ? 0 : -1;
            SearchStatus.Text = query.Length == 0 && area == SearchArea.Text
                ? Tr("Введите текст для поиска в файлах рабочей папки", "Enter text to search the working folder’s files")
                : Tr("Ничего не найдено", "No results found");
            SearchStatus.Visibility = VisibleIf(matches.Count == 0);
            SearchLimitHint.Visibility = VisibleIf(limited);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException)
        {
            if (ct.IsCancellationRequested) return;
            SearchStatus.Text = Tr("Не удалось выполнить поиск. ", "Search could not finish. ") + ex.Message;
        }
    }

    void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        Action? shortcut = (Keyboard.Modifiers, key) switch
        {
            (ModifierKeys.Control, Key.N) => StartNewChat,
            (ModifierKeys.Control | ModifierKeys.Shift, Key.O) => () => OpenWorkDir_Click(this, e),
            (ModifierKeys.Control, Key.OemComma) => ShowSettings,
            _ => null,
        };
        if (shortcut != null)
        {
            CloseSearch();
            shortcut();
            e.Handled = true;
            return;
        }
        if (key == Key.Escape || Keyboard.Modifiers == ModifierKeys.Control && key == Key.K) CloseSearch();
        else if (key is Key.Down or Key.Up)
        {
            int count = SearchResults.Items.Count;
            if (count > 0)
            {
                SearchResults.SelectedIndex = Math.Clamp(SearchResults.SelectedIndex + (key == Key.Down ? 1 : -1), 0, count - 1);
                SearchResults.ScrollIntoView(SearchResults.SelectedItem);
            }
        }
        else if (key == Key.Enter && SearchResults.SelectedItem is SearchMatch match) OpenSearchMatch(match);
        else return;
        e.Handled = true;
    }

    void SearchResults_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(SearchResults, source) is ListBoxItem { Content: SearchMatch match })
            OpenSearchMatch(match);
    }

    async void OpenChatSearchMatch(SearchMatch match)
    {
        if (WaitForChatFile(ChatFileBusy)) return;
        await RunChatOperationAsync(() => OpenChatAsync(match.Path));
        if (!IsCurrent(match.Path) || match.Query.Length == 0) return;
        var history = _agent.History;
        bool Matches(int at) => at >= 0 && at < history.Count && JsonUtil.Str(history[at]["role"]) is "user" or "assistant" &&
            (AgentRunner.ExtractText(history[at]["content"]) ?? "").Contains(match.Query, StringComparison.OrdinalIgnoreCase);
        int index = Matches(match.MessageIndex) ? match.MessageIndex : Enumerable.Range(0, history.Count).FirstOrDefault(Matches, -1);
        if (index < 0) return;
        StopChatMotion();
        _autoScroll = false;
        int version = ++_historyVersion;
        _historyPagePending = false;
        FrameworkElement page = ChatPanel;
        if (index < _tailStart)
        {
            _changingHistory = true;
            try
            {
                ReleaseStickyProgress();
                ChatPages.Children.RemoveRange(0, ChatPages.Children.Count - 1);
                _historyPages.Clear();
                int start = index;
                while (start > 0 && !_renderedHistory.IsUser(start)) start--;
                int end = FindHistoryPageEnd(start);
                var historyPage = RenderHistoryPage(start, end);
                _historyPages.AddLast((start, end, historyPage));
                ChatPages.Children.Insert(0, historyPage);
                ChatPanel.Visibility = VisibleIf(end == _tailStart);
                page = historyPage;
                UpdateChangesPill();
            }
            finally { _changingHistory = false; }
        }
        var agent = _agent;
        await Dispatcher.InvokeAsync(() =>
        {
            if (agent != _agent || version != _historyVersion) return;
            var target = SearchNodes(page, logical: true).OfType<FrameworkElement>()
                .FirstOrDefault(view => Equals(view.GetValue(SearchMessageProperty), index)) ?? page;
            for (DependencyObject? parent = target; parent != null; parent = ParentOf(parent) ?? LogicalTreeHelper.GetParent(parent))
                if (parent is Expander expander) expander.IsExpanded = true;
            ChatScroll.UpdateLayout();
            HighlightChatMatch(target, match.Query);
        }, DispatcherPriority.Loaded);
    }

    void HighlightChatMatch(FrameworkElement target, string query)
    {
        _changingHistory = true;
        try
        {
            foreach (var node in SearchNodes(target))
            {
                Rect position;
                FrameworkElement view;
                if (node is RichTextBox rich && FindChatText(rich.Document, query) is { } range)
                {
                    rich.Focus();
                    rich.Selection.Select(range.Start, range.End);
                    position = range.Start.GetCharacterRect(LogicalDirection.Forward);
                    view = rich;
                }
                else if (node is TextBox text && text.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) is >= 0 and var at)
                {
                    text.IsInactiveSelectionHighlightEnabled = true;
                    text.Focus();
                    text.Select(at, query.Length);
                    position = text.GetRectFromCharacterIndex(at);
                    view = text;
                }
                else continue;
                double top = view.TranslatePoint(position.IsEmpty ? new() : position.TopLeft, ChatScroll).Y;
                ChatScroll.ScrollToVerticalOffset(Math.Max(0, ChatScroll.VerticalOffset + top - 72));
                ChatScroll.UpdateLayout();
                break;
            }
        }
        finally
        {
            _changingHistory = false;
            _autoScroll = false;
            UpdateScrollDownButton();
        }
    }

    static TextRange? FindChatText(FlowDocument document, string query)
    {
        var text = new StringBuilder();
        var segments = new List<(int Offset, string Text, TextPointer Start)>();
        for (var pointer = document.ContentStart; pointer != null; pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var run = pointer.GetTextInRun(LogicalDirection.Forward);
                segments.Add((text.Length, run, pointer));
                text.Append(run);
            }
            else if (pointer.GetAdjacentElement(LogicalDirection.Forward) is Paragraph or LineBreak) text.Append('\n');
        }
        int at = text.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var first = segments.FirstOrDefault(segment => segment.Offset <= at && at < segment.Offset + segment.Text.Length);
        int end = at + query.Length;
        var last = segments.FirstOrDefault(segment => segment.Offset < end && end <= segment.Offset + segment.Text.Length);
        if (first.Start == null || last.Start == null) return null;
        return new(first.Start.GetPositionAtOffset(at - first.Offset), last.Start.GetPositionAtOffset(end - last.Offset));
    }

    static IEnumerable<DependencyObject> SearchNodes(DependencyObject parent, bool logical = false)
    {
        yield return parent;
        var children = logical ? LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>() :
            Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(parent)).Select(index => VisualTreeHelper.GetChild(parent, index));
        foreach (var child in children)
            foreach (var node in SearchNodes(child, logical)) yield return node;
    }

    void OpenSearchMatch(SearchMatch match)
    {
        CloseSearch();
        if (match.Area == SearchArea.Actions)
        {
            if (match.Path == "new") StartNewChat();
            else if (match.Path == "settings") ShowSettings();
            else OpenWorkDir_Click(this, new RoutedEventArgs());
            return;
        }
        if (match.Area == SearchArea.Chats)
        {
            OpenChatSearchMatch(match);
            return;
        }
        try
        {
            var path = _toolContext.Resolve(match.Path);
            _toolContext.GuardSecrets(path);
            _toolContext.GuardAppFiles(path);
            if (match.Area == SearchArea.Folders) FileActions.OpenFolder(path);
            else
            {
                if (match.Area == SearchArea.Text) ShowPreview(path);
                else OpenInApp(path);
                if (match.Line > 0 && PreviewBody.Content is Grid grid && grid.Children.OfType<TextBox>().FirstOrDefault() is { } text)
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsShown(PreviewPane) || PreviewBody.Content != grid || match.Line > text.LineCount) return;
                        int start = text.GetCharacterIndexFromLineIndex(match.Line - 1);
                        if (start < 0) return;
                        text.Focus();
                        text.Select(start, text.GetLineLength(match.Line - 1));
                        text.ScrollToLine(match.Line - 1);
                    }, DispatcherPriority.Loaded);
            }
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException) { ShowToast(ex.Message, ToastKind.Warning); }
    }
}
