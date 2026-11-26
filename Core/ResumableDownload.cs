using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SharpAgent.Core;

static class ResumableDownload
{
    internal const int Attempts = 5;

    internal static async Task FetchAsync(HttpClient http, string url, string path, long size, string expected,
        Action<long, int>? progress, CancellationToken ct, bool supportsRanges = true)
    {
        await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 64 * 1024, useAsync: true);
        if (output.Length > size) output.SetLength(0);
        var buffer = new byte[64 * 1024];
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            long offset = output.Position = output.Length;
            progress?.Invoke(offset, attempt);
            if (offset == size) break;
            try
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timeout = TimeSpan.FromSeconds(90);
                idle.CancelAfter(timeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (offset > 0 && supportsRanges) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                long end = size;
                if (offset > 0 && supportsRanges)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent ||
                        range is not { Unit: "bytes", From: { } start, To: { } last, Length: { } total } ||
                        start != offset || total != size || last < start || last >= size)
                        throw new IOException("Сервер не подтвердил продолжение загрузки. Скачанная часть сохранена.");
                    end = last + 1;
                }
                else if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("Сервер вернул неожиданный ответ при загрузке файла.");
                await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                // Silero serves the whole file on retries, so keep the saved bytes and skip that prefix.
                if (!supportsRanges)
                    for (long remaining = offset; remaining > 0;) remaining -= await ReadAsync(remaining).ConfigureAwait(false);
                while (output.Position < end)
                {
                    int read = await ReadAsync(end - output.Position).ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Invoke(output.Position, attempt);
                }
                if (output.Position < size) throw new HttpRequestException("Сервер передал только часть файла.");
                break;

                async ValueTask<int> ReadAsync(long remaining)
                {
                    int read;
                    try { read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), idle.Token).ConfigureAwait(false); }
                    catch (IOException ex) { throw new HttpRequestException("Соединение прервалось при загрузке файла.", ex); }
                    if (read == 0) throw new HttpRequestException("Сервер прервал загрузку файла.");
                    idle.CancelAfter(timeout);
                    return read;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is OperationCanceledException ||
                ex is HttpRequestException requestError && (requestError.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                    (int?)requestError.StatusCode >= 500)))
            {
                if (attempt == Attempts)
                    throw new IOException($"Загрузка прервана после {Attempts} попыток. Скачанная часть сохранена.", ex);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
            }
        }
        output.Position = 0;
        if (!Convert.ToHexString(await SHA256.HashDataAsync(output, ct).ConfigureAwait(false)).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            output.SetLength(0);
            throw new IOException("Контрольная сумма скачанного файла не совпала.");
        }
    }
}
