using System.Net.Security;
using System.Net.Sockets;

namespace SharpAgent.Core;

// Chromium does not authenticate SOCKS5 connections. Only WebView2 processes belonging to this session may use the loopback adapter.
internal sealed class BrowserProxy : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _browsers = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public void Allow(uint pid) => _browsers.TryAdd((int)pid, 0);

    public BrowserProxy()
    {
        _listener.Start();
        _ = AcceptAsync(_stop.Token);
    }

    async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client, ct));
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
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
            using var upstream = new TcpClient();
            using var closeRemote = ct.Register(upstream.Dispose);
            var local = client.GetStream();
            var hello = new byte[2];
            await local.ReadExactlyAsync(hello, ct);
            if (hello[0] != 5) return;
            var modes = new byte[hello[1]];
            await local.ReadExactlyAsync(modes, ct);
            if (!modes.Contains((byte)0)) return;
            await local.WriteAsync(new byte[] { 5, 0 }, ct);
            var proxy = await AppNetwork.BrowserProxyAsync(ct);
            var uri = proxy.Address!;
            await upstream.ConnectAsync(uri.IdnHost, uri.Port, ct);
            Stream remote = upstream.GetStream();
            if (uri.Scheme == "https")
            {
                var tls = new SslStream(remote);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.IdnHost }, ct);
                remote = tls;
            }
            using (remote)
            {
                var cred = proxy.Credentials as NetworkCredential;
                if (uri.Scheme == "socks5")
                {
                    byte method = cred == null ? (byte)0 : (byte)2;
                    await remote.WriteAsync(new byte[] { 5, 1, method }, ct);
                    await remote.ReadExactlyAsync(hello, ct);
                    if (hello[0] != 5 || hello[1] != method) return;
                    if (cred != null)
                    {
                        var user = Encoding.UTF8.GetBytes(cred.UserName);
                        var password = Encoding.UTF8.GetBytes(cred.Password);
                        if (user.Length > 255 || password.Length > 255) return;
                        await remote.WriteAsync((byte[])[1, (byte)user.Length, ..user, (byte)password.Length, ..password], ct);
                        await remote.ReadExactlyAsync(hello, ct);
                        if (hello[0] != 1 || hello[1] != 0) return;
                    }
                }
                else if (!await HttpTunnelAsync(local, remote, cred, ct)) return;
                var send = local.CopyToAsync(remote, ct);
                var receive = remote.CopyToAsync(local, ct);
                await Task.WhenAny(send, receive);
                client.Dispose();
                upstream.Dispose();
                await Task.WhenAll(send, receive);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or
            InvalidOperationException or HttpRequestException or System.Security.Authentication.AuthenticationException or Win32Exception or ArgumentException) { }
    }

    static async Task<bool> HttpTunnelAsync(Stream local, Stream remote, NetworkCredential? credential, CancellationToken ct)
    {
        var header = new byte[4];
        await local.ReadExactlyAsync(header, ct);
        if (header[0] != 5 || header[1] != 1) return false;
        var one = new byte[1];
        if (header[3] == 3) await local.ReadExactlyAsync(one, ct);
        int size = header[3] switch { 1 => 4, 3 => one[0], 4 => 16, _ => 0 };
        if (size == 0) return false;
        var address = new byte[size];
        await local.ReadExactlyAsync(address, ct);
        var host = header[3] == 3 ? Encoding.ASCII.GetString(address) : new IPAddress(address).ToString();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;
        await local.ReadExactlyAsync(header.AsMemory(0, 2), ct);
        var authority = (host.Contains(':') ? $"[{host}]" : host) + ":" + (header[0] * 256 + header[1]);
        var auth = credential == null ? "" : "Proxy-Authorization: Basic " +
            Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.UserName + ":" + credential.Password)) + "\r\n";
        await remote.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n{auth}\r\n"), ct);
        var response = new StringBuilder();
        for (int end = 0; end != 4 && response.Length < 16384;)
        {
            await remote.ReadExactlyAsync(one, ct);
            char ch = (char)one[0];
            response.Append(ch);
            end = ch == "\r\n\r\n"[end] ? end + 1 : ch == '\r' ? 1 : 0;
        }
        var reply = response.ToString();
        if (!reply.EndsWith("\r\n\r\n", StringComparison.Ordinal) ||
            !(reply.StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal) || reply.StartsWith("HTTP/1.0 200 ", StringComparison.Ordinal))) return false;
        await local.WriteAsync(new byte[] {5,0,0,1,0,0,0,0,0,0}, ct);
        return true;
    }

    bool OwnsPeer(IPEndPoint peer)
    {
        int size = 0;
        GetExtendedTcpTable(null, ref size, false, 2, 5, 0);
        if (size < 4) return false;
        var table = new byte[size];
        if (GetExtendedTcpTable(table, ref size, false, 2, 5, 0) != 0) return false;
        for (int offset = 4; offset + 24 <= size; offset += 24)
        {
            if (table[offset + 8] * 256 + table[offset + 9] != peer.Port ||
                table[offset + 16] * 256 + table[offset + 17] != Port || BitConverter.ToUInt32(table, offset + 4) != 0x0100007f) continue;
            int pid = BitConverter.ToInt32(table, offset + 20);
            for (int depth = 0; pid > 0 && depth < 8; depth++)
            {
                using var process = Process.GetProcessById(pid);
                if (!process.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) return false;
                var info = new IntPtr[6];
                if (NtQueryInformationProcess(process.Handle, 0, info, 6 * IntPtr.Size, out _) != 0) return false;
                if (_browsers.ContainsKey(pid)) return info[5].ToInt32() == Environment.ProcessId;
                pid = info[5].ToInt32();
            }
        }
        return false;
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
