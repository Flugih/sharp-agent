namespace SharpAgent;

public partial class MainWindow
{
    // All nodes go into one .env line and one menu, so a huge subscription is cut.
    const int MaxVpnProfiles = 200;
    List<VpnProfile> _vpnProfiles = [];
    VpnProfile? _vpnSelection;
    (string Path, string Sha256) _networkEngineApproval;
    int _networkGeneration;

    void LoadNetworkSettings(NetworkSettings settings, string savedProfiles)
    {
        _networkGeneration++;
        NetworkProxyType.Content = settings.Scheme is "http" or "https" or "socks5" ? settings.Scheme.ToUpperInvariant() : "SOCKS5";
        NetworkHost.Text = settings.Host;
        NetworkPort.Text = settings.Port;
        NetworkUser.Text = settings.User;
        NetworkPassword.Password = settings.Password;
        NetworkSubscription.Password = settings.Subscription;
        NetworkEngine.Text = File.Exists(settings.Engine) ? settings.Engine : "";
        _networkEngineApproval = (settings.Engine, settings.EngineSha256);
        _vpnSelection = VpnProfile.Restore(settings.Node);
        _vpnProfiles = savedProfiles.Split(';', StringSplitOptions.RemoveEmptyEntries).Take(MaxVpnProfiles)
            .Select(VpnProfile.Restore).OfType<VpnProfile>().ToList();
        if (_vpnSelection is { } saved)
        {
            _vpnSelection = _vpnProfiles.Find(profile => profile.Save() == settings.Node);
            if (_vpnSelection == null) _vpnProfiles.Add(_vpnSelection = saved);
        }
        ShowVpnSelection();
        NetworkImportStatus.Text = "";
        NetworkTestStatus.Text = "";
        NetworkImportButton.IsEnabled = NetworkTestButton.IsEnabled = true;
        var mode = NetworkModeChoices.Children.OfType<RadioButton>().First(item => Equals(item.Tag, settings.Mode.ToString()));
        if (mode.IsChecked == true && settings.Mode == NetworkMode.Vpn) FindNetworkEngine();
        else mode.IsChecked = true;
    }

    NetworkSettings ReadNetworkSettings()
    {
        var mode = NetworkModeChoices.Children.OfType<RadioButton>().FirstOrDefault(item => item.IsChecked == true)?.Tag as string;
        string engine = NetworkEngine.Text.Trim().Trim('"');
        return new(Enum.TryParse<NetworkMode>(mode, out var selectedMode) ? selectedMode : NetworkMode.System,
            ((string?)NetworkProxyType.Content ?? "SOCKS5").ToLowerInvariant(),
            NetworkHost.Text.Trim(), NetworkPort.Text.Trim(), NetworkUser.Text, NetworkPassword.Password,
            NetworkSubscription.Password.Trim(), engine, _vpnSelection?.Save() ?? "", ApprovedEngineHash(engine));
    }

    void NetworkMode_Checked(object sender, RoutedEventArgs e)
    {
        var button = (RadioButton)sender;
        NetworkProxyFields.Visibility = VisibleIf(Equals(button.Tag, "Proxy"));
        bool vpn = Equals(button.Tag, "Vpn");
        NetworkVpnFields.Visibility = VisibleIf(vpn);
        if (vpn) FindNetworkEngine();
        NetworkTestStatus.Text = "";
    }

    void NetworkProxyType_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = NetworkProxyType, MinWidth = NetworkProxyType.ActualWidth };
        foreach (var type in new[] { "SOCKS5", "HTTP", "HTTPS" })
            menu.Items.Add(FileActions.Item(type, null, () =>
            {
                NetworkProxyType.Content = type;
                NetworkTestStatus.Text = "";
            }));
        menu.IsOpen = true;
    }

    async void NetworkBrowse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "sing-box.exe|sing-box.exe" };
        if (picker.ShowDialog(this) != true) return;
        NetworkEngine.Text = picker.FileName;
        try
        {
            int generation = _networkGeneration;
            var hash = await SingBox.FingerprintAsync(picker.FileName);
            if (generation != _networkGeneration || NetworkEngine.Text != picker.FileName) return;
            _networkEngineApproval = (picker.FileName, hash);
            FindNetworkEngine();
            NetworkTestStatus.Text = "";
        }
        catch (SingBoxException ex) { SetStatus(NetworkTestStatus, ex.Message, "DangerText"); }
    }

    static void SetStatus(TextBlock status, string text, string brush)
    {
        status.SetResourceReference(TextBlock.ForegroundProperty, brush);
        status.Text = text;
    }

    string ApprovedEngineHash(string path) => string.Equals(path, _networkEngineApproval.Path, StringComparison.OrdinalIgnoreCase)
        ? _networkEngineApproval.Sha256 : "";

    void FindNetworkEngine()
    {
        var engine = SingBox.Find(NetworkEngine.Text.Trim().Trim('"'));
        if (engine != null) NetworkEngine.Text = engine;
        NetworkEnginePicker.Visibility = VisibleIf(engine == null || ApprovedEngineHash(engine).Length == 0);
        NetworkEngineFound.Visibility = VisibleIf(engine != null);
        NetworkEngineFound.ToolTip = engine;
    }

    async Task<NetworkSettings?> ApproveNetworkEngineAsync(NetworkSettings settings)
    {
        if (settings.Mode != NetworkMode.Vpn) return settings;
        int generation = _networkGeneration;
        var before = ReadNetworkSettings();
        var hash = await SingBox.FingerprintAsync(settings.Engine);
        if (generation != _networkGeneration || !SettingsOpen || ReadNetworkSettings() != before) return null;
        if (string.Equals(hash, settings.EngineSha256, StringComparison.OrdinalIgnoreCase)) return settings;
        if (!DarkDialog.Ask(this, "Разрешить запуск sing-box.exe?",
                "Sharp Agent будет запускать этот файл для VPN. Подтвердите путь, если вы доверяете источнику файла.",
                "Разрешить запуск", code: settings.Engine, delayed: true)) return null;
        _networkEngineApproval = (settings.Engine, hash);
        FindNetworkEngine();
        return settings with { EngineSha256 = hash };
    }

    void NetworkDownload_Click(object sender, RoutedEventArgs e) => FileActions.OpenUrl("https://github.com/SagerNet/sing-box/releases/latest");

    async void NetworkImport_Click(object sender, RoutedEventArgs e)
    {
        int generation = _networkGeneration;
        string source = NetworkSubscription.Password.Trim();
        NetworkImportButton.IsEnabled = false;
        SetStatus(NetworkImportStatus, "Загружаю подписку…", "TextMuted");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            if (_state.SubscriptionDeviceId is null || _state.SubscriptionDeviceId == Guid.Empty)
            {
                _state.SubscriptionDeviceId = Guid.NewGuid();
                _state.Save();
            }
            var (profiles, skipped) = await VpnSubscription.LoadAsync(source, _state.SubscriptionDeviceId.Value, timeout.Token);
            if (generation != _networkGeneration || !SettingsOpen || source != NetworkSubscription.Password.Trim()) return;
            string? current = _vpnSelection?.Save();
            _vpnProfiles = profiles.Take(MaxVpnProfiles).ToList();
            _vpnSelection = _vpnProfiles.Find(profile => profile.Save() == current) ?? _vpnProfiles[0];
            ShowVpnSelection();
            SetStatus(NetworkImportStatus, $"Серверов: {profiles.Count}." +
                (profiles.Count > MaxVpnProfiles ? $" Сохранены первые {MaxVpnProfiles}." : "") +
                (skipped > 0 ? $" Пропущено несовместимых записей: {skipped}." : ""), "Success");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            if (generation != _networkGeneration || !SettingsOpen || source != NetworkSubscription.Password.Trim()) return;
            SetStatus(NetworkImportStatus, ex is InvalidOperationException ? ex.Message : "Не удалось загрузить подписку.", "DangerText");
        }
        finally
        {
            if (generation == _networkGeneration) NetworkImportButton.IsEnabled = true;
        }
    }

    void ShowVpnSelection()
    {
        NetworkVpnServer.Content = _vpnSelection?.Label ?? "Сначала загрузите серверы";
        NetworkVpnServer.IsEnabled = _vpnProfiles.Count != 0;
        NetworkTestStatus.Text = "";
    }

    void NetworkSubscription_Changed(object sender, RoutedEventArgs e)
    {
        NetworkSubscription.Tag = null;
        _vpnProfiles = [];
        _vpnSelection = null;
        NetworkImportStatus.Text = "";
        ShowVpnSelection();
    }

    void NetworkVpnServer_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = NetworkVpnServer, MaxHeight = 320, MinWidth = NetworkVpnServer.ActualWidth };
        foreach (var server in _vpnProfiles)
            menu.Items.Add(FileActions.Item(server.Label.Replace("_", "__"), server == _vpnSelection ? Glyphs.Check : null, () =>
            {
                _vpnSelection = server;
                ShowVpnSelection();
            }));
        menu.IsOpen = true;
    }

    string NetworkProbeUrl() => (_settingsProvider switch
    {
        AgentProvider.Tooken => SettingsTookenBaseUrl(),
        AgentProvider.ChatGpt => ChatGptClient.BaseUrl,
        _ => AppConfig.DefaultBaseUrlOf(_settingsProvider),
    }).TrimEnd('/') + "/models";

    void ConnectionFieldChanged(object sender, RoutedEventArgs e)
    {
        NetworkTestStatus.Text = "";
        if (sender == KeyPasswordBox || sender == KeyTextBox || sender == BaseUrlBox)
            SetTestResult("", null);
    }

    async void NetworkTest_Click(object sender, RoutedEventArgs e)
    {
        int generation = _networkGeneration;
        var settings = ReadNetworkSettings();
        var endpoint = NetworkProbeUrl();
        if (settings.Mode == NetworkMode.Vpn)
        {
            FindNetworkEngine();
            settings = ReadNetworkSettings();
        }
        NetworkTestButton.IsEnabled = false;
        SetStatus(NetworkTestStatus, "Проверяю сетевой маршрут…", "TextMuted");
        try
        {
            if (await ApproveNetworkEngineAsync(settings.Validate()) is not { } approved)
            {
                NetworkTestStatus.Text = "";
                return;
            }
            settings = approved;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                throw new InvalidOperationException("Проверьте адрес API в настройках провайдера.");
            var status = await AppNetwork.CheckAsync(settings, endpoint, timeout.Token);
            if (NetworkProbeUrl() != endpoint) return;
            if (generation != _networkGeneration || !SettingsOpen || ReadNetworkSettings() != settings) return;
            if (status == HttpStatusCode.Forbidden)
                SetStatus(NetworkTestStatus, "Сервер отклонил запрос (HTTP 403). Проверьте доступ к API по этому маршруту.", "Warning");
            else SetStatus(NetworkTestStatus, "API доступен по выбранному маршруту.", "Success");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException)
        {
            if (generation != _networkGeneration || !SettingsOpen || ReadNetworkSettings() != settings || NetworkProbeUrl() != endpoint) return;
            SetStatus(NetworkTestStatus, ex switch
            {
                InvalidOperationException or NetworkRouteException => ex.Message,
                OperationCanceledException => "Проверка подключения не завершилась вовремя.",
                HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "Не удалось разрешить адрес сервера API.",
                HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } => "Прокси не смог открыть соединение с сервером API.",
                _ => "Нет ответа от сервера API по выбранному маршруту.",
            }, "DangerText");
            if (ex is NetworkRouteException { InvalidEngine: true })
            {
                NetworkEngineFound.Visibility = Visibility.Collapsed;
                NetworkEnginePicker.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (generation == _networkGeneration)
            {
                if (ReadNetworkSettings() != settings) NetworkTestStatus.Text = "";
                NetworkTestButton.IsEnabled = true;
            }
        }
    }
}
