namespace SharpAgent;

public partial class MainWindow
{
    sealed class BrowserTab
    {
        public readonly StackPanel Header = new() { Orientation = Orientation.Horizontal, Margin = new(1, 0, 4, 0) };
        public readonly RadioButton Select = new() { Style = Theme.Style("PillToggle"), GroupName = "BrowserTabs", MaxWidth = 165 };
        public readonly TextBlock Title = new() { Text = "Новая вкладка", TextTrimming = TextTrimming.CharacterEllipsis };
        public BrowserView? Page;
        public Uri? Url;
        public string? Error;
    }

    readonly List<BrowserTab> _tabs = [];
    BrowserTab? _browserTab;
    BrowserProxy? _browserProxy;
    NetworkSettings? _browserNetwork;
    bool _browserExpanded;
    double _browserWidth;

    void BrowserOpen_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlay();
        CloseSubagents(false);
        ClosePreview();
        BrowserPane.Visibility = Visibility.Visible;
        ApplyPreviewLayout();
        if (_browserTab == null) AddBrowserTab(null);
        else SelectBrowserTab(_browserTab);
        BrowserAddress.Focus();
    }

    void HideBrowser()
    {
        if (!IsShown(BrowserPane)) return;
        BrowserPane.Visibility = Visibility.Collapsed;
        ExpandBrowser(false);
    }

    void BrowserClose_Click(object sender, RoutedEventArgs e) { HideBrowser(); InputBox.Focus(); }
    void BrowserNew_Click(object sender, RoutedEventArgs e) => AddBrowserTab(null);
    void BrowserNetwork_Click(object sender, RoutedEventArgs e) => OpenSettings(NetworkTestButton);
    void BrowserExpand_Click(object sender, RoutedEventArgs e) => ExpandBrowser(!_browserExpanded);

    void ExpandBrowser(bool expanded)
    {
        _browserExpanded = expanded;
        UpdateBrowserExpand();
        ApplyPreviewLayout();
    }

    void UpdateBrowserExpand()
    {
        BrowserExpand.Content = _browserExpanded ? Glyphs.BackToWindow : Glyphs.FullScreen;
        AutomationProperties.SetName(BrowserExpand, _browserExpanded ? "Вернуть браузер в боковую панель" : "Развернуть браузер");
    }

    void AddBrowserTab(Uri? url)
    {
        var tab = new BrowserTab { Url = url };
        tab.Select.Content = tab.Title;
        tab.Select.Checked += (_, _) => SelectBrowserTab(tab);
        tab.Header.Children.Add(tab.Select);
        var close = Theme.IconButton(Glyphs.Cancel, "Закрыть вкладку", 23, 9);
        close.Click += (_, _) => CloseBrowserTab(tab);
        tab.Header.Children.Add(close);
        _tabs.Add(tab);
        BrowserTabs.Children.Add(tab.Header);
        tab.Select.IsChecked = true;
        tab.Header.BringIntoView();
        if (url == null) BrowserAddress.Focus();
    }

    void CloseBrowserTab(BrowserTab tab)
    {
        int index = _tabs.IndexOf(tab);
        _tabs.RemoveAt(index);
        BrowserTabs.Children.Remove(tab.Header);
        ReleaseBrowserPage(tab);
        if (tab != _browserTab) return;
        _browserTab = null;
        if (_tabs.Count == 0) AddBrowserTab(null);
        else _tabs[Math.Min(index, _tabs.Count - 1)].Select.IsChecked = true;
    }

    void SelectBrowserTab(BrowserTab tab)
    {
        if (_browserTab?.Page is { } old) old.Visibility = Visibility.Collapsed;
        _browserTab = tab;
        if (tab.Page != null) tab.Page.Visibility = Visibility.Visible;
        else if (tab.Url != null && tab.Error == null) OpenBrowserPage(tab, tab.Url);
        UpdateBrowserToolbar(true);
    }

    void UpdateBrowserToolbar(bool address)
    {
        if (_browserTab is not { } tab) return;
        bool empty = tab.Url == null;
        Grid.SetRow(BrowserToolbar, empty ? 2 : 1);
        BrowserToolbar.VerticalAlignment = empty ? VerticalAlignment.Center : VerticalAlignment.Top;
        BrowserToolbar.Margin = empty ? new(14, 0, 14, 0) : new(2, 5, 2, 6);
        BrowserNavigation.Visibility = VisibleIf(!empty);
        BrowserNetworkButton.ToolTip = _config.Network.Mode switch
        {
            NetworkMode.Vpn => "Сеть: VPN", NetworkMode.Proxy => "Сеть: " + _config.Network.Scheme.ToUpperInvariant(),
            NetworkMode.Direct => "Сеть: напрямую", _ => "Сеть: системные настройки",
        };
        BrowserPages.IsHitTestVisible = !empty;
        BrowserBack.IsEnabled = tab.Page?.CanBack == true;
        BrowserForward.IsEnabled = tab.Page?.CanForward == true;
        if (address) BrowserAddress.Text = tab.Url?.AbsoluteUri ?? "";
        BrowserError.Text = tab.Error ?? "";
        BrowserError.Visibility = VisibleIf(tab.Error != null);
    }

    void BrowserAddress_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _browserTab == null) return;
        e.Handled = true;
        var text = BrowserAddress.Text.Trim();
        if (text.Length == 0) return;
        Uri? uri;
        if (!text.Contains("://") && !text.Any(char.IsWhiteSpace) && Uri.TryCreate("https://" + text, UriKind.Absolute, out var host) &&
            (host.Host.Contains('.') || host.IsLoopback || host.HostNameType == UriHostNameType.IPv6)) uri = host;
        else if (!Uri.TryCreate(text, UriKind.Absolute, out uri))
            uri = new Uri("https://www.google.com/search?q=" + Uri.EscapeDataString(text));
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
        {
            ShowToast("Введите адрес сайта с http или https либо поисковый запрос.", ToastKind.Warning);
            return;
        }
        OpenBrowserPage(_browserTab, uri);
        UpdateBrowserToolbar(true);
    }

    void OpenBrowserPage(BrowserTab tab, Uri uri)
    {
        tab.Url = uri;
        tab.Error = null;
        if (tab.Page != null) { tab.Page.Navigate(uri); return; }
        try
        {
            var mode = _config.Network.Mode;
            var proxy = mode is NetworkMode.Proxy or NetworkMode.Vpn ? _browserProxy ??= new() : null;
            var page = tab.Page = new BrowserView(uri, mode, proxy, _toolContext.WorkingDirectory);
            tab.Title.Text = uri.Host;
            page.Changed += () =>
            {
                if (tab.Page != page) return;
                bool editing = BrowserAddress.IsKeyboardFocused && BrowserAddress.Text != tab.Url?.AbsoluteUri;
                tab.Url = page.Address;
                tab.Title.ToolTip = tab.Title.Text = page.Title;
                if (_browserTab == tab) UpdateBrowserToolbar(!editing);
            };
            page.Popup += url => { if (tab.Page == page) AddBrowserTab(url); };
            page.Failed += message => Dispatcher.InvokeAsync(() =>
            {
                if (tab.Page != page) return;
                ReleaseBrowserPage(tab);
                tab.Error = message;
                if (_browserTab == tab) UpdateBrowserToolbar(false);
            });
            BrowserPages.Children.Add(page);
        }
        catch (System.Net.Sockets.SocketException) { tab.Error = "Не удалось создать локальное подключение к прокси."; }
    }

    void BrowserBack_Click(object sender, RoutedEventArgs e) => _browserTab?.Page?.Back();
    void BrowserForward_Click(object sender, RoutedEventArgs e) => _browserTab?.Page?.Forward();
    void BrowserReload_Click(object sender, RoutedEventArgs e)
    {
        if (_browserTab?.Page is { } page) page.Reload();
        else if (_browserTab?.Url is { } url) { OpenBrowserPage(_browserTab, url); UpdateBrowserToolbar(true); }
    }

    void ReleaseBrowserPage(BrowserTab tab)
    {
        if (tab.Page is not { } page) return;
        tab.Page = null;
        BrowserPages.Children.Remove(page);
        page.Dispose();
    }

    void ResetBrowserNetwork(bool closing = false)
    {
        foreach (var tab in _tabs) { ReleaseBrowserPage(tab); tab.Error = null; }
        if (closing) _browserProxy?.Dispose();
    }
}
