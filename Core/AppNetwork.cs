using System.Net.Http.Headers;
using System.Net.Sockets;

namespace SharpAgent.Core;

internal enum NetworkMode { System, Direct, Proxy, Vpn }

internal sealed record NetworkSettings(NetworkMode Mode = NetworkMode.System, string Scheme = "socks5", string Host = "",
    string Port = "1080", string User = "", string Password = "", string Subscription = "", string Engine = "", string Node = "",
    string EngineSha256 = "")
{
    internal static NetworkSettings Load(Func<string, string?> read) => new(
        Enum.TryParse<NetworkMode>(read("AGENT_NETWORK_MODE"), true, out var mode) && Enum.IsDefined(mode) ? mode : NetworkMode.System,
        read("AGENT_PROXY_SCHEME") ?? "socks5", read("AGENT_PROXY_HOST") ?? "", read("AGENT_PROXY_PORT") ?? "1080",
        read("AGENT_PROXY_USER") ?? "", read("AGENT_PROXY_PASSWORD") ?? "", read("AGENT_VPN_SUBSCRIPTION") ?? "",
        read("AGENT_VPN_ENGINE") ?? "", read("AGENT_VPN_NODE") ?? "", read("AGENT_VPN_ENGINE_SHA256") ?? "");

    internal void WriteTo(Dictionary<string, string?> values)
    {
        values["AGENT_NETWORK_MODE"] = Mode.ToString().ToLowerInvariant();
        values["AGENT_PROXY_SCHEME"] = Scheme;
        values["AGENT_PROXY_HOST"] = Host;
        values["AGENT_PROXY_PORT"] = Port;
        values["AGENT_PROXY_USER"] = User;
        values["AGENT_PROXY_PASSWORD"] = Password;
        values["AGENT_VPN_SUBSCRIPTION"] = Subscription;
        values["AGENT_VPN_ENGINE"] = Engine;
        values["AGENT_VPN_ENGINE_SHA256"] = EngineSha256;
        values["AGENT_VPN_NODE"] = Node;
    }

    internal WebProxy Proxy()
    {
        var host = Host.Trim('[', ']');
        if (Scheme is not ("http" or "https" or "socks5") || Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            !int.TryParse(Port, out int port) || port is < 1 or > 65_535)
            throw new InvalidOperationException("Укажите адрес и порт прокси от 1 до 65535.");
        if (User.Length == 0 && Password.Length != 0)
            throw new InvalidOperationException("Укажите логин для пароля прокси.");
        return new(new UriBuilder(Scheme, host, port).Uri) { Credentials = User.Length == 0 ? null : new NetworkCredential(User, Password) };
    }

    internal string? Blocked { get; init; }

    internal NetworkSettings Validate()
    {
        if (Blocked != null) throw new InvalidOperationException(Blocked);
        if (Mode == NetworkMode.Proxy) _ = Proxy();
        if (Mode != NetworkMode.Vpn) return this;
        if (Node.Length == 0) throw new InvalidOperationException("Загрузите подписку и выберите сервер VPN.");
        var engine = SingBox.Find(Engine) ?? throw new SingBoxException("sing-box.exe не найден. Укажите путь или скачайте Windows-версию.");
        return engine == Engine ? this : this with { Engine = engine, EngineSha256 = "" };
    }
}

internal sealed class NetworkRouteException(string message, bool invalidEngine = false) : HttpRequestException(message)
{
    internal bool InvalidEngine => invalidEngine;
}

internal static class AppNetwork
{
    static Route _current = new(new NetworkSettings());
    static readonly Lazy<BrowserProxy> CommandRelay = new(() => new(commands: true));
    static readonly IntPtr EngineJob = CreateEngineJob();

    internal static bool CustomRoute => Volatile.Read(ref _current).Settings.Mode is NetworkMode.Proxy or NetworkMode.Vpn;
    internal static CancellationToken RouteLifetime => Volatile.Read(ref _current).Lifetime;
    internal static string? RouteFailure => Volatile.Read(ref _current).Failure;

    internal static async Task<WebProxy> BrowserProxyAsync(CancellationToken ct)
    {
        var route = Volatile.Read(ref _current);
        return (await route.Connect().WaitAsync(ct).ConfigureAwait(false)).Proxy as WebProxy
            ?? throw new NetworkRouteException("Выбранный прокси недоступен.");
    }

    internal static void Apply(NetworkSettings settings)
    {
        if (Volatile.Read(ref _current).Settings == settings) return;
        Interlocked.Exchange(ref _current, new Route(settings)).Dispose();
    }

    // Commands get the address of a password-less local relay: null keeps system settings, "" means no proxy.
    internal static string? ConfigureCommand(ProcessStartInfo start)
    {
        var mode = Volatile.Read(ref _current).Settings.Mode;
        if (mode == NetworkMode.System) return null;
        var address = mode == NetworkMode.Direct ? "" : $"http://127.0.0.1:{CommandRelay.Value.Port}";
        foreach (var key in new[] { "http_proxy", "https_proxy", "all_proxy" })
            if (address.Length == 0) start.Environment.Remove(key);
            else start.Environment[key] = address;
        start.Environment["no_proxy"] = address.Length == 0 ? "*" : "localhost,127.0.0.1,::1";
        return address;
    }

    internal static NetworkCredential? ProxyCredential =>
        Volatile.Read(ref _current).Settings is { Mode: NetworkMode.Proxy, User.Length: > 0 } settings ? new(settings.User, settings.Password) : null;

    internal static void Stop() => Apply(new NetworkSettings());

    internal static HttpClient Client(TimeSpan? timeout = null, int? maxBytes = null, bool redirects = true, bool cookies = true,
        DecompressionMethods decompression = DecompressionMethods.None, Action<HttpRequestHeaders>? headers = null)
    {
        var client = new HttpClient(new RoutingHandler(null, redirects, cookies, decompression));
        if (timeout is { } wait) client.Timeout = wait;
        if (maxBytes is { } size) client.MaxResponseContentBufferSize = size;
        headers?.Invoke(client.DefaultRequestHeaders);
        return client;
    }

    internal static async Task<HttpStatusCode> CheckAsync(NetworkSettings settings, string endpoint, CancellationToken ct)
    {
        var current = Volatile.Read(ref _current);
        using var trial = current.Settings == settings ? null : new Route(settings);
        using var client = new HttpClient(new RoutingHandler(trial ?? current, false, false, DecompressionMethods.None));
        var route = trial ?? current;
        route.ClearFailure();
        try
        {
            using var reply = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!reply.IsSuccessStatusCode && reply.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                throw new InvalidOperationException($"API провайдера вернул HTTP {(int)reply.StatusCode}.");
            return reply.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            if (ex is not NetworkRouteException { InvalidEngine: true } && route.Failure is { } detail)
                throw new NetworkRouteException(detail);
            throw;
        }
    }

    sealed record Connection(bool UseProxy, IWebProxy? Proxy);

    sealed class Route : IDisposable
    {
        readonly CancellationTokenSource _stop = new();
        Task<Connection>? _connection;
        Process? _process;
        volatile string? _failure;
        readonly object _gate = new();
        bool _disposed;

        internal Route(NetworkSettings settings) => Settings = settings;
        internal NetworkSettings Settings { get; }
        internal CancellationToken Lifetime => _stop.Token;
        internal string? Failure => _failure;
        internal void ClearFailure() => _failure = null;

        internal Task<Connection> Connect()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_connection is { IsCompletedSuccessfully: true } && _process?.HasExited != true ||
                    _connection is { IsCompleted: false }) return _connection;
                Close(_process);
                _process = null;
                ClearFailure();
                return _connection = OpenAsync(Settings, Attach, ReportFailure, _stop.Token);
            }
        }

        internal bool IsCurrent(Task<Connection> attempt)
        {
            lock (_gate) return !_disposed && ReferenceEquals(_connection, attempt);
        }

        void Attach(Process process)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _process = process;
            }
        }

        void ReportFailure(string? line)
        {
            if (line == null) return;
            var message = line.ToLowerInvariant();
            string? detail = message switch
            {
                _ when message.Contains("decode config") || message.Contains("unknown field") => "sing-box не принял конфигурацию выбранного сервера.",
                _ when message.Contains("websocket") && (message.Contains("handshake") || message.Contains("status")) =>
                    "VPN-сервер отклонил подключение WebSocket. Загрузите подписку заново или выберите другой сервер.",
                _ when message.Contains("certificate") || message.Contains("tls handshake") || message.Contains("reality verification") =>
                    "VPN-сервер не прошёл проверку TLS. Проверьте выбранный профиль.",
                _ when message.Contains("connection refused") => "VPN-сервер отклонил сетевое подключение.",
                _ when message.Contains("timeout") || message.Contains("deadline exceeded") => "VPN-сервер не ответил вовремя.",
                _ => null,
            };
            if (detail != null) _failure = detail;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _stop.Cancel();
                Close(_process);
                _stop.Dispose();
            }
        }
    }

    sealed class RoutingHandler(Route? fixedRoute, bool redirects, bool cookies, DecompressionMethods decompression) : HttpMessageHandler
    {
        readonly object _gate = new();
        HttpMessageInvoker? _invoker;
        Connection? _connection;
        bool _disposed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            while (true)
            {
                var route = fixedRoute ?? Volatile.Read(ref _current);
                Task<Connection> attempt;
                Connection connection;
                try
                {
                    attempt = route.Connect();
                    connection = await attempt.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (fixedRoute == null && !ReferenceEquals(route, Volatile.Read(ref _current)) &&
                                           ex is ObjectDisposedException or OperationCanceledException or HttpRequestException) { continue; }
                Task<HttpResponseMessage> response;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (fixedRoute == null && !ReferenceEquals(route, Volatile.Read(ref _current))) continue;
                    if (!route.IsCurrent(attempt)) continue;
                    if (_invoker == null || !ReferenceEquals(_connection, connection))
                    {
                        _invoker?.Dispose();
                        _invoker = new(new SocketsHttpHandler
                        {
                            UseProxy = connection.UseProxy, Proxy = connection.Proxy, AllowAutoRedirect = redirects,
                            UseCookies = cookies, AutomaticDecompression = decompression, ConnectTimeout = TimeSpan.FromSeconds(15),
                        });
                        _connection = connection;
                    }
                    response = _invoker.SendAsync(request, ct);
                }
                try { return await response.ConfigureAwait(false); }
                catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ProxyTunnelError)
                {
                    throw new NetworkRouteException(route.Failure ?? "Прокси или VPN не смог подключиться к серверу.");
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                lock (_gate)
                {
                    _disposed = true;
                    _invoker?.Dispose();
                }
            base.Dispose(disposing);
        }
    }

    static async Task<Connection> OpenAsync(NetworkSettings settings, Action<Process> attach, Action<string?> reportFailure, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            settings = settings.Validate();
            if (settings.Mode != NetworkMode.Vpn)
                return new(settings.Mode != NetworkMode.Direct, settings.Mode == NetworkMode.Proxy ? settings.Proxy() : null);
            if (settings.EngineSha256.Length == 0)
                throw new SingBoxException("Подтвердите запуск sing-box.exe в настройках сети.");
            using var engine = SingBox.OpenFile(settings.Engine);
            if (!string.Equals(await SingBox.FingerprintAsync(engine, ct).ConfigureAwait(false), settings.EngineSha256, StringComparison.OrdinalIgnoreCase))
                throw new SingBoxException("Файл sing-box.exe изменился. Подтвердите его заново в настройках сети.");
            ct.ThrowIfCancellationRequested();
            await SingBox.CheckVersionAsync(settings.Engine, ct).ConfigureAwait(false);
            var profile = VpnProfile.Restore(settings.Node) ?? throw new InvalidOperationException("Импортируйте сервер VPN заново.");
            var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            int number = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var config = profile.Config(number, password);
            var start = new ProcessStartInfo(settings.Engine)
            {
                ArgumentList = { "run", "-c", "stdin" }, WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false), UseShellExecute = false, WorkingDirectory = AppPaths.AppFolder,
            };
            ct.ThrowIfCancellationRequested();
            var started = process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить sing-box.");
            AssignProcessToJobObject(EngineJob, process.Handle);
            using var stopProcess = ct.Register(() => Kill(started));
            process.ErrorDataReceived += (_, e) => reportFailure(e.Data);
            process.OutputDataReceived += (_, e) => reportFailure(e.Data);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await process.StandardInput.WriteAsync(config.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            while (!process.HasExited)
            {
                using var probe = new TcpClient();
                try { await probe.ConnectAsync(IPAddress.Loopback, number, timeout.Token).ConfigureAwait(false); }
                catch (SocketException) { }
                // Another program may grab the freed port first; it must not receive the SOCKS password.
                if (!probe.Connected || BrowserProxy.Owner(number, ((IPEndPoint)probe.Client.LocalEndPoint!).Port) != process.Id)
                {
                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                    continue;
                }
                if (process.HasExited) break;
                var connection = new Connection(true, new WebProxy($"socks5://127.0.0.1:{number}")
                    { Credentials = new NetworkCredential("sharp-agent", password) });
                attach(process);
                process = null;
                return connection;
            }
            throw new InvalidOperationException("sing-box завершился при запуске. Проверьте версию программы и профиль сервера.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception or ArgumentException or SocketException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            throw new NetworkRouteException(ex is InvalidOperationException ? ex.Message : "Не удалось запустить сетевое подключение Sharp Agent.",
                invalidEngine: ex is SingBoxException);
        }
        finally { Close(process); }
    }

    static void Close(Process? process)
    {
        if (process == null) return;
        Kill(process);
        process.Dispose();
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    // Windows closes the job handle when Sharp Agent exits in any way and then ends sing-box with it.
    static IntPtr CreateEngineJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new long[18];
        limits[2] = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE in BasicLimitInformation.LimitFlags
        SetInformationJobObject(job, 9, limits, IntPtr.Size == 8 ? 144 : 112);
        return job;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int type, long[] info, int size);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
