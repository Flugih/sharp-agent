namespace SharpAgent;

public partial class MainWindow
{
    // All nodes go into one .env line and one menu, so a huge subscription is cut.
    const int MaxVpnProfiles = 200;
    static (NetworkMode Value, string Label)[] NetworkModes =>
        [(NetworkMode.System, Tr("Системные настройки", "System settings")), (NetworkMode.Direct, Tr("Напрямую", "Direct")), (NetworkMode.Proxy, Tr("Прокси", "Proxy")), (NetworkMode.Vpn, "VPN")];
    static readonly (string? Value, string Label)[] ProxyTypes = [("SOCKS5", "SOCKS5"), ("HTTP", "HTTP"), ("HTTPS", "HTTPS")];
    NetworkMode _networkMode;
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
        ShowNetworkMode(settings.Mode);
    }

    NetworkSettings ReadNetworkSettings()
    {
        string engine = NetworkEngine.Text.Trim().Trim('"');
        return new(_networkMode, ((string?)NetworkProxyType.Content ?? "SOCKS5").ToLowerInvariant(),
            NetworkHost.Text.Trim(), NetworkPort.Text.Trim(), NetworkUser.Text, NetworkPassword.Password,
            NetworkSubscription.Password.Trim(), engine, _vpnSelection?.Save() ?? "", ApprovedEngineHash(engine));
    }

    void NetworkModeButton_Click(object sender, RoutedEventArgs e) => PickChoice(NetworkModeButton, NetworkModes, _networkMode, ShowNetworkMode);

    void ShowNetworkMode(NetworkMode mode)
    {
        _networkMode = mode;
        ShowChoice(NetworkModeButton, NetworkModes, mode);
        NetworkProxyFields.Visibility = VisibleIf(mode == NetworkMode.Proxy);
        NetworkVpnFields.Visibility = VisibleIf(mode == NetworkMode.Vpn);
        if (mode == NetworkMode.Vpn) FindNetworkEngine();
        NetworkTestStatus.Text = "";
    }

    void NetworkProxyType_Click(object sender, RoutedEventArgs e) =>
        PickChoice(NetworkProxyType, ProxyTypes, NetworkProxyType.Content as string, type =>
        {
            NetworkProxyType.Content = type;
            NetworkTestStatus.Text = "";
        });

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
        if (!DarkDialog.Ask(this, Tr("Разрешить запуск sing-box.exe?", "Allow Sharp Agent to start sing-box.exe?"),
                Tr("Sharp Agent будет запускать этот файл для VPN. Подтвердите путь, если вы доверяете источнику файла.", "Sharp Agent will run this file for VPN access. Confirm the path if you trust where the file came from."),
                Tr("Разрешить запуск", "Allow launch"), code: settings.Engine, delayed: true)) return null;
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
        SetStatus(NetworkImportStatus, Tr("Загружаю подписку…", "Loading subscription…"), "TextMuted");
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
            SetStatus(NetworkImportStatus, Tr($"Серверов: {profiles.Count}.", $"Servers: {profiles.Count}.") +
                (profiles.Count > MaxVpnProfiles ? Tr($" Сохранены первые {MaxVpnProfiles}.", $" Saved the first {MaxVpnProfiles}.") : "") +
                (skipped > 0 ? Tr($" Пропущено несовместимых записей: {skipped}.", $" Skipped {skipped} incompatible entries.") : ""), "Success");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            if (generation != _networkGeneration || !SettingsOpen || source != NetworkSubscription.Password.Trim()) return;
            SetStatus(NetworkImportStatus, ex is InvalidOperationException ? ex.Message : Tr("Не удалось загрузить подписку.", "Could not load the subscription."), "DangerText");
        }
        finally
        {
            if (generation == _networkGeneration) NetworkImportButton.IsEnabled = true;
        }
    }

    void ShowVpnSelection()
    {
        NetworkVpnServer.Content = _vpnSelection?.Label ?? Tr("Сначала загрузите серверы", "Load the servers first");
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

    void NetworkVpnServer_Click(object sender, RoutedEventArgs e) =>
        PickChoice(NetworkVpnServer, _vpnProfiles.Select(server => ((VpnProfile?)server, server.Label)).ToArray(), _vpnSelection, server =>
        {
            _vpnSelection = server;
            ShowVpnSelection();
        });

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
        SetStatus(NetworkTestStatus, Tr("Проверяю сетевой маршрут…", "Checking network route…"), "TextMuted");
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
                throw new InvalidOperationException(Tr("Проверьте адрес API в настройках провайдера.", "Check the API address in provider settings."));
            var status = await AppNetwork.CheckAsync(settings, endpoint, timeout.Token);
            if (NetworkProbeUrl() != endpoint) return;
            if (generation != _networkGeneration || !SettingsOpen || ReadNetworkSettings() != settings) return;
            if (status == HttpStatusCode.Forbidden)
                SetStatus(NetworkTestStatus, Tr("Сервер отклонил запрос (HTTP 403). Проверьте доступ к API по этому маршруту.", "The server rejected the request (HTTP 403). Check API access over this route."), "Warning");
            else SetStatus(NetworkTestStatus, Tr("API доступен по выбранному маршруту.", "The API is reachable over this route."), "Success");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException)
        {
            if (generation != _networkGeneration || !SettingsOpen || ReadNetworkSettings() != settings || NetworkProbeUrl() != endpoint) return;
            SetStatus(NetworkTestStatus, ex switch
            {
                InvalidOperationException or NetworkRouteException => ex.Message,
                OperationCanceledException => Tr("Проверка подключения не завершилась вовремя.", "The connection check timed out."),
                HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => Tr("Не удалось разрешить адрес сервера API.", "Could not resolve the API server address."),
                HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } => Tr("Прокси не смог открыть соединение с сервером API.", "The proxy could not connect to the API server."),
                _ => Tr("Нет ответа от сервера API по выбранному маршруту.", "The API server did not respond over the selected route."),
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
