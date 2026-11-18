using System.Net.Security;
using System.Net.Sockets;

namespace SharpAgent.Core;

// Local clients do not authenticate, so the relay serves only this session's WebView2 processes or, for commands, processes started by Sharp Agent.
internal sealed class BrowserProxy : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _browsers = new();
    readonly bool _commands;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public void Allow(uint pid) => _browsers.TryAdd((int)pid, 0);

    public BrowserProxy(bool commands = false)
    {
        _commands = commands;
        _listener.Start();
        _ = AcceptAsync(_stop.Token);
    }

    async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client, ct));
            }
            // A connection reset while waiting in the backlog fails only that accept.
            catch (Exception ex) when (ex is SocketException || ct.IsCancellationRequested) { }
    }

    async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        try
        {
            if (!OwnsPeer((IPEndPoint)client.Client.RemoteEndPoint!)) return;
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, AppNetwork.RouteLifetime);
            ct = lifetime.Token;
            using var closeLocal = ct.Register(client.Dispose);
            var local = client.GetStream();
            var first = new byte[1];
            await local.ReadExactlyAsync(first, ct);
            bool socks = first[0] == 5;
            var (host, port, request) = socks ? await SocksRequestAsync(local, ct) : HttpRequest(await ReadHeadAsync(local, ((char)first[0]).ToString(), ct));
            if (host == null) return;
            Stream remote;
            try { remote = await TunnelAsync(host, port, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or SocketException or HttpRequestException or
                System.Security.Authentication.AuthenticationException)
            {
                await RefuseAsync(local, socks, port, ex is NetworkRouteException ? ex.Message : AppNetwork.RouteFailure ??
                    $"Сервер {host}:{port} недоступен через выбранный прокси или VPN.", ct);
                return;
            }
            using var tunnel = remote;
            using var closeRemote = ct.Register(remote.Dispose);
            if (socks) await local.WriteAsync(SocksReply(0), ct);
            else if (request == null) await local.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), ct);
            else await remote.WriteAsync(request, ct);
            var send = local.CopyToAsync(remote, ct);
            var receive = remote.CopyToAsync(local, ct);
            await Task.WhenAny(send, receive);
            client.Dispose();
            remote.Dispose();
            await Task.WhenAll(send, receive);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or
            InvalidOperationException or HttpRequestException or System.Security.Authentication.AuthenticationException or Win32Exception or ArgumentException) { }
    }

    static byte[] SocksReply(byte status) => [5, status, 0, 1, 0, 0, 0, 0, 0, 0];

    // Plain HTTP clients, including the browser on port 80, get a readable page; a TLS client can only be told that SOCKS failed.
    static async Task RefuseAsync(Stream local, bool socks, int port, string reason, CancellationToken ct)
    {
        if (socks)
        {
            await local.WriteAsync(SocksReply((byte)(port == 80 ? 0 : 1)), ct);
            if (port != 80 || await ReadHeadAsync(local, "", ct) == null) return;
        }
        var body = Encoding.UTF8.GetBytes("Sharp Agent не смог открыть адрес через выбранную сеть.\n" + reason);
        await local.WriteAsync((byte[])[.. Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n"), .. body], ct);
    }

    static async Task<(string? Host, int Port, byte[]? Request)> SocksRequestAsync(Stream local, CancellationToken ct)
    {
        var header = new byte[4];
        await local.ReadExactlyAsync(header.AsMemory(0, 1), ct);
        var modes = new byte[header[0]];
        await local.ReadExactlyAsync(modes, ct);
        if (!modes.Contains((byte)0)) return default;
        await local.WriteAsync(new byte[] { 5, 0 }, ct);
        await local.ReadExactlyAsync(header, ct);
        if (header[0] != 5 || header[1] != 1) return default;
        if (header[3] == 3) await local.ReadExactlyAsync(header.AsMemory(0, 1), ct);
        int size = header[3] switch { 1 => 4, 3 => header[0], 4 => 16, _ => 0 };
        if (size == 0) return default;
        var address = new byte[size + 2];
        await local.ReadExactlyAsync(address, ct);
        var host = header[3] == 3 ? Encoding.ASCII.GetString(address, 0, size) : new IPAddress(address[..size]).ToString();
        return Uri.CheckHostName(host) == UriHostNameType.Unknown ? default : (host, address[size] * 256 + address[size + 1], null);
    }

    // Plain HTTP requests are rewritten to origin form and closed after one response, so a reused connection cannot reach another host.
    static (string? Host, int Port, byte[]? Request) HttpRequest(string? head)
    {
        var lines = head?.Split("\r\n") ?? [];
        if (lines.FirstOrDefault()?.Split(' ') is not [var method, var target, var version]) return default;
        if (method == "CONNECT")
            return Uri.TryCreate("http://" + target, UriKind.Absolute, out var authority) && authority.PathAndQuery == "/"
                ? (authority.IdnHost.Trim('[', ']'), authority.Port, null) : default;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "http") return default;
        var request = new StringBuilder($"{method} {uri.PathAndQuery} {version}\r\n");
        foreach (var line in lines.Skip(1))
            if (line.Length > 0 && !line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)) request.Append(line).Append("\r\n");
        return (uri.IdnHost.Trim('[', ']'), uri.Port, Encoding.Latin1.GetBytes(request.Append("Connection: close\r\n\r\n").ToString()));
    }

    static async Task<string?> ReadHeadAsync(Stream stream, string start, CancellationToken ct)
    {
        var head = new StringBuilder(start);
        var one = new byte[1];
        for (int end = 0; end != 4; )
        {
            if (head.Length >= 16384) return null;
            await stream.ReadExactlyAsync(one, ct);
            char ch = (char)one[0];
            head.Append(ch);
            end = ch == "\r\n\r\n"[end] ? end + 1 : ch == '\r' ? 1 : 0;
        }
        return head.ToString();
    }

    static async Task<Stream> TunnelAsync(string host, int port, CancellationToken ct)
    {
        var proxy = await AppNetwork.BrowserProxyAsync(ct);
        var uri = proxy.Address!;
        var cred = proxy.Credentials as NetworkCredential;
        var upstream = new TcpClient();
        Stream remote = Stream.Null;
        try
        {
            await upstream.ConnectAsync(uri.IdnHost, uri.Port, ct);
            remote = upstream.GetStream();
            if (uri.Scheme == "https")
            {
                var tls = new SslStream(remote);
                remote = tls;
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.IdnHost }, ct);
            }
            if (uri.Scheme == "socks5")
            {
                await SocksConnectAsync(remote, cred, host, port, ct);
                return remote;
            }
            var authority = (host.Contains(':') ? $"[{host}]" : host) + ":" + port;
            var auth = cred == null ? "" : "Proxy-Authorization: Basic " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(cred.UserName + ":" + cred.Password)) + "\r\n";
            await remote.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{auth}\r\n"), ct);
            var reply = await ReadHeadAsync(remote, "", ct);
            return reply != null && (reply.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal) || reply.StartsWith("HTTP/1.0 200 ", StringComparison.Ordinal))
                ? remote : throw new IOException();
        }
        catch
        {
            remote.Dispose();
            upstream.Dispose();
            throw;
        }
    }

    static async Task SocksConnectAsync(Stream remote, NetworkCredential? cred, string host, int port, CancellationToken ct)
    {
        byte method = cred == null ? (byte)0 : (byte)2;
        var reply = new byte[4];
        await remote.WriteAsync(new byte[] { 5, 1, method }, ct);
        await remote.ReadExactlyAsync(reply.AsMemory(0, 2), ct);
        if (reply[0] != 5 || reply[1] != method) throw new IOException();
        if (cred != null)
        {
            var user = Encoding.UTF8.GetBytes(cred.UserName);
            var password = Encoding.UTF8.GetBytes(cred.Password);
            if (user.Length > 255 || password.Length > 255) throw new IOException();
            await remote.WriteAsync((byte[])[1, (byte)user.Length, ..user, (byte)password.Length, ..password], ct);
            await remote.ReadExactlyAsync(reply.AsMemory(0, 2), ct);
            if (reply[0] != 1 || reply[1] != 0) throw new IOException();
        }
        var name = Encoding.ASCII.GetBytes(host);
        if (name.Length > 255) throw new IOException();
        await remote.WriteAsync((byte[])[5, 1, 0, 3, (byte)name.Length, ..name, (byte)(port >> 8), (byte)port], ct);
        await remote.ReadExactlyAsync(reply, ct);
        if (reply[0] != 5 || reply[1] != 0) throw new IOException();
        if (reply[3] == 3) await remote.ReadExactlyAsync(reply.AsMemory(0, 1), ct);
        await remote.ReadExactlyAsync(new byte[reply[3] switch { 1 => 4, 3 => reply[0], 4 => 16, _ => throw new IOException() } + 2], ct);
    }

    bool OwnsPeer(IPEndPoint peer)
    {
        int pid = Owner(peer.Port, Port), child = 0;
        var started = DateTime.MaxValue;
        for (int depth = 0; pid > 0 && depth < 16; depth++)
        {
            using var process = Process.GetProcessById(pid);
            // A parent started after its child is a reused process ID, not the real parent.
            if (process.StartTime > started) return false;
            if (pid == Environment.ProcessId) return depth > 0 && (_commands || _browsers.ContainsKey(child));
            if (!_commands && !process.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) return false;
            started = process.StartTime;
            var info = new IntPtr[6];
            if (NtQueryInformationProcess(process.Handle, 0, info, 6 * IntPtr.Size, out _) != 0) return false;
            (child, pid) = (pid, info[5].ToInt32());
        }
        return false;
    }

    // Process on the local side of a loopback TCP connection.
    internal static int Owner(int localPort, int remotePort)
    {
        int size = 0;
        GetExtendedTcpTable(null, ref size, false, 2, 5, 0);
        if (size < 4) return 0;
        var table = new byte[size];
        if (GetExtendedTcpTable(table, ref size, false, 2, 5, 0) != 0) return 0;
        for (int offset = 4; offset + 24 <= size; offset += 24)
            if (table[offset + 8] * 256 + table[offset + 9] == localPort && table[offset + 16] * 256 + table[offset + 17] == remotePort &&
                BitConverter.ToUInt32(table, offset + 4) == 0x0100007f) return BitConverter.ToInt32(table, offset + 20);
        return 0;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    [DllImport("iphlpapi.dll")] static extern int GetExtendedTcpTable([Out] byte[]? table, ref int size, bool sort, int family, int type, int reserved);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int type, [Out] IntPtr[] info, int size, out int length);
}
