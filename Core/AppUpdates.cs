using System.Net.NetworkInformation;
using System.Security.Cryptography;

namespace SharpAgent.Core;

sealed class AppUpdates(string folder, Version installed)
{
    const string Repository = "noneandundefined/sharp-agent", InstallerName = "SharpAgent-Setup.exe", ManifestName = "pending.json";
    const string PartialName = InstallerName + ".part";
    internal const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";

    internal sealed record Release(Version Version, long Size, string Hash)
    {
        internal string DownloadUrl => $"https://github.com/{Repository}/releases/download/v{Version}/{InstallerName}";
    }

    internal async Task<Release?> PrepareAsync(HttpClient http, CancellationToken ct)
    {
        var cached = ReadPending();
        if (cached is { } ready)
        {
            using var verified = await OpenVerifiedAsync(ready, ct).ConfigureAwait(false);
            if (verified != null) return ready;
        }
        await WaitForNetworkAsync(ct).ConfigureAwait(false);
        Release? update;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            update = response.StatusCode == HttpStatusCode.NotFound ? null : FindRelease(ParseObject(
                await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync(ct).ConfigureAwait(false)), installed);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            update = cached;
        }
        if (update == null) return null;
        Directory.CreateDirectory(LocalPath(folder));
        File.Delete(Entry(InstallerName));
        if (update != cached)
        {
            File.Delete(Entry(PartialName));
            var pending = Entry(ManifestName + ".tmp");
            File.WriteAllText(pending, new JsonObject
            {
                ["tag"] = "v" + update.Version, ["size"] = update.Size, ["sha256"] = update.Hash,
            }.ToJsonString());
            File.Move(pending, Entry(ManifestName), overwrite: true);
        }
        var partial = Entry(PartialName);
        while (true)
        {
            try
            {
                await ResumableDownload.FetchAsync(http, update.DownloadUrl, partial, update.Size, update.Hash, null, ct).ConfigureAwait(false);
                break;
            }
            catch (IOException ex) when (!ct.IsCancellationRequested && !NetworkInterface.GetIsNetworkAvailable() &&
                ex.InnerException is HttpRequestException or OperationCanceledException)
            {
                await WaitForNetworkAsync(ct).ConfigureAwait(false);
            }
        }
        ct.ThrowIfCancellationRequested();
        File.Move(Entry(PartialName), Entry(InstallerName), overwrite: true);
        return update;
    }

    internal async Task<FileStream?> OpenVerifiedAsync(Release update, CancellationToken ct)
    {
        var path = Entry(InstallerName);
        if (!File.Exists(path)) return null;
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        bool verified = false;
        try
        {
            verified = file.Length == update.Size && Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false))
                .Equals(update.Hash, StringComparison.OrdinalIgnoreCase);
            return verified ? file : null;
        }
        finally { if (!verified) await file.DisposeAsync().ConfigureAwait(false); }
    }

    internal static Release? FindRelease(JsonObject? json, Version installed)
    {
        if (json == null || Bool(json["draft"]) == true || Bool(json["prerelease"]) == true || json["assets"] is not JsonArray assets) return null;
        var installer = assets.OfType<JsonObject>().FirstOrDefault(item => Str(item["name"]) == InstallerName && Str(item["state"]) == "uploaded");
        return Str(installer?["digest"]) is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? Validate(Str(json["tag_name"]), Long(installer?["size"]), digest["sha256:".Length..], installed) : null;
    }

    Release? ReadPending()
    {
        var manifest = Entry(ManifestName);
        if (!File.Exists(manifest)) return null;
        var json = ParseObject(File.ReadAllText(manifest));
        var update = Validate(Str(json?["tag"]), Long(json?["size"]), Str(json?["sha256"]), installed);
        if (update != null) return update;
        File.Delete(Entry(InstallerName));
        File.Delete(Entry(PartialName));
        File.Delete(manifest);
        return null;
    }

    static Release? Validate(string? tag, long size, string? hash, Version installed) =>
        tag is ['v', .. var number] && Version.TryParse(number, out var version) && version > installed &&
        number == version.ToString() && size > 0 && hash is { Length: 64 } && hash.All(Uri.IsHexDigit)
            ? new(version, size, hash.ToUpperInvariant()) : null;

    string Entry(string name) => LocalPath(Path.Combine(folder, name));

    static string LocalPath(string path) => PathHelper.SamePath(path, PathHelper.FinalPath(path))
        ? path : throw new IOException("Путь для обновления Sharp Agent ведёт в другую папку.");

    static async Task WaitForNetworkAsync(CancellationToken ct)
    {
        if (NetworkInterface.GetIsNetworkAvailable()) return;
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void AvailabilityChanged(object? _, NetworkAvailabilityEventArgs e)
        {
            if (e.IsAvailable) connected.TrySetResult();
        }
        NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) await connected.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally { NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged; }
    }
}
