using System.Collections.Concurrent;
using System.Net.Sockets;

namespace SharpAgent.Ui;

static partial class LinkTitles
{
    static readonly HttpClient Client = AppNetwork.Client(TimeSpan.FromSeconds(6), redirects: false, cookies: false,
        decompression: DecompressionMethods.All, headers: headers => headers.UserAgent.ParseAdd("SharpAgent/1.0"));
    static readonly ConcurrentDictionary<Uri, string> Cache = new();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 500)]
    private static partial Regex TitlePattern();

    public static bool IsWebUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length > 0;

    public static Task<string?> GetAsync(Uri uri) =>
        Cache.TryGetValue(uri, out var cached) ? Task.FromResult<string?>(cached) : Task.Run(() => FetchAsync(uri));

    static async Task<string?> FetchAsync(Uri uri)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var ct = timeout.Token;
        try
        {
            var current = uri;
            for (int redirects = 0; redirects <= 3 && IsWebUri(current); redirects++)
            {
                using var response = await Client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.Headers.Location is { } location && (int)response.StatusCode is >= 300 and < 400)
                {
                    current = new Uri(current, location);
                    if (!IsWebUri(current)) return null;
                    if (AppNetwork.CustomRoute)
                    {
                        if (!string.Equals(current.IdnHost, uri.IdnHost, StringComparison.OrdinalIgnoreCase)) return null;
                    }
                    else if (await IsInternalAsync(current, ct) && !await IsInternalAsync(uri, ct)) return null;
                    continue;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml"))
                    return null;
                var charset = response.Content.Headers.ContentType.CharSet?.Trim('"');
                var encoding = charset is { Length: > 0 } ? Encoding.GetEncoding(charset) : Encoding.UTF8;
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), encoding, detectEncodingFromByteOrderMarks: true);
                var prefix = new StringBuilder();
                var buffer = new char[4096];
                while (prefix.Length < 128 * 1024 && await reader.ReadAsync(buffer.AsMemory(), ct) is > 0 and var count)
                {
                    if (TitlePattern().Match(prefix.Append(buffer, 0, count).ToString()) is not { Success: true } match) continue;
                    var title = Regex.Replace(WebUtility.HtmlDecode(match.Groups[1].Value), @"\s+", " ").Trim();
                    if (title.Length == 0) return null;
                    if (title.Length > 180) title = title[..179] + "…";
                    if (Cache.Count < 128) Cache.TryAdd(uri, title);
                    return title;
                }
                return null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ArgumentException
                                   or RegexMatchTimeoutException or FormatException) { }
        return null;
    }

    static async Task<bool> IsInternalAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            return (await Dns.GetHostAddressesAsync(uri.IdnHost, ct)).Any(IsInternal);
        }
        catch (SocketException)
        {
            return uri.IsLoopback;
        }
    }

    static bool IsInternal(IPAddress address) => address.IsIPv4MappedToIPv6 ? IsInternal(address.MapToIPv4())
        : IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal ||
          address.Equals(IPAddress.IPv6Any) || address.GetAddressBytes() is [0 or 10, _, _, _] or [169, 254, _, _] or [192, 168, _, _]
              or [172, >= 16 and < 32, _, _] or [100, >= 64 and < 128, _, _];
}
