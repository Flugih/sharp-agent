using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpAgent.Ui;

internal static partial class LinkTitles
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    })
    {
        Timeout = TimeSpan.FromSeconds(6),
        DefaultRequestHeaders = { { "User-Agent", "SharpAgent/1.0" } },
    };
    private static readonly ConcurrentDictionary<Uri, string> Cache = new();

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 500)]
    private static partial Regex TitlePattern();

    public static bool IsWebUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length > 0;

    public static async Task<string?> GetAsync(Uri uri)
    {
        if (Cache.TryGetValue(uri, out var cached)) return cached;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            var current = uri;
            for (int redirects = 0; redirects <= 3 && IsWebUri(current); redirects++)
            {
                using var response = await Client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.Headers.Location is { } location && (int)response.StatusCode is >= 300 and < 400)
                {
                    var next = new Uri(current, location);
                    if (next.IsLoopback && !uri.IsLoopback) return null;
                    current = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml"))
                    return null;
                var charset = response.Content.Headers.ContentType.CharSet?.Trim('"');
                var encoding = charset is { Length: > 0 } ? Encoding.GetEncoding(charset) : Encoding.UTF8;
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
                var title = await ReadTitleAsync(reader, timeout.Token);
                if (title == null) return null;
                if (Cache.Count < 128) Cache.TryAdd(uri, title);
                return title;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or ArgumentException
                                   or RegexMatchTimeoutException or FormatException)
        {
        }
        return null;
    }

    private static async Task<string?> ReadTitleAsync(TextReader reader, CancellationToken ct)
    {
        var prefix = new StringBuilder();
        var buffer = new char[4096];
        while (prefix.Length < 128 * 1024 && await reader.ReadAsync(buffer.AsMemory(), ct) is > 0 and var count)
        {
            prefix.Append(buffer, 0, count);
            if (TitlePattern().Match(prefix.ToString()) is not { Success: true } match) continue;
            var title = Regex.Replace(WebUtility.HtmlDecode(match.Groups[1].Value), @"\s+", " ").Trim();
            return title.Length switch
            {
                0 => null,
                > 180 => title[..179] + "…",
                _ => title,
            };
        }
        return null;
    }
}
