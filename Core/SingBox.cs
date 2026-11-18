using Folder = System.Environment.SpecialFolder;

namespace SharpAgent.Core;

internal sealed class SingBoxException(string message) : InvalidOperationException(message);

internal static partial class SingBox
{
    const string FileName = "sing-box.exe";

    internal static FileStream OpenFile(string path)
    {
        try { return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new SingBoxException("Не удалось прочитать sing-box.exe. Выберите доступный файл."); }
    }

    internal static async Task<string> FingerprintAsync(Stream file, CancellationToken ct) =>
        Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));

    internal static Task<string> FingerprintAsync(string path) => Task.Run(async () =>
    {
        using var file = OpenFile(path);
        try { return await FingerprintAsync(file, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException) { throw new SingBoxException("Не удалось прочитать sing-box.exe. Выберите доступный файл."); }
    });

    [GeneratedRegex(@"^sing-box version (?<version>\d+\.\d+(?:\.\d+)?)(?:[-+\s]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionLine();

    internal static async Task CheckVersionAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var start = new ProcessStartInfo(path)
        {
            ArgumentList = { "version" }, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(start) ?? throw new SingBoxException("Не удалось запустить sing-box.exe.");
            try
            {
                process.BeginErrorReadLine();
                var buffer = new char[128];
                int read = await process.StandardOutput.ReadBlockAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var match = VersionLine().Match(new string(buffer, 0, read));
                if (process.ExitCode != 0 || !Version.TryParse(match.Groups["version"].Value, out var version))
                    throw new SingBoxException("Этот файл не отвечает как sing-box. Выберите sing-box.exe из официального Windows-архива.");
                if (version < new Version(1, 12))
                    throw new SingBoxException($"Найдена версия sing-box {version}. Нужна версия 1.12 или новее.");
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
        }
        catch (Win32Exception ex)
        {
            throw new SingBoxException($"Windows не смогла запустить sing-box.exe (код {ex.NativeErrorCode}).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SingBoxException("sing-box.exe не ответил на проверку версии. Выберите другой файл.");
        }
    }

    internal static string? Find(string configured)
    {
        if (Exists(configured)) return configured;
        foreach (var directory in new[] { AppContext.BaseDirectory, AppPaths.AppFolder }
                     .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)))
        {
            var candidate = In(directory.Trim().Trim('"'), FileName);
            if (candidate != null) return candidate;
        }
        var local = Environment.GetFolderPath(Folder.LocalApplicationData);
        var user = Environment.GetFolderPath(Folder.UserProfile);
        foreach (var root in new[] { Environment.GetFolderPath(Folder.ProgramFiles), Environment.GetFolderPath(Folder.ProgramFilesX86),
                     Path.Combine(local, "Programs"), Path.Combine(user, "Downloads"), AppPaths.AppFolder, AppContext.BaseDirectory })
        {
            if (In(root, "sing-box", FileName) is { } installed) return installed;
            foreach (var folder in Subfolders(root, "sing-box-*"))
                if (In(folder, FileName) is { } extracted) return extracted;
        }
        var winget = Path.Combine(local, "Microsoft", "WinGet");
        if (In(winget, "Links", FileName) is { } link) return link;
        foreach (var package in Subfolders(Path.Combine(winget, "Packages"), "SagerNet.sing-box_*"))
        {
            if (In(package, FileName) is { } binary) return binary;
            foreach (var folder in Subfolders(package, "sing-box-*"))
                if (In(folder, FileName) is { } extracted) return extracted;
        }
        return In(Environment.GetEnvironmentVariable("SCOOP") ?? Path.Combine(user, "scoop"), "apps", "sing-box", "current", FileName);
    }

    static bool Exists(string path) => Path.IsPathFullyQualified(path) &&
        Path.GetFileName(path).Equals(FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    static string? In(string root, params string[] parts)
    {
        if (!Path.IsPathFullyQualified(root)) return null;
        var path = Path.Combine(root, Path.Combine(parts));
        return Exists(path) ? path : null;
    }

    static string[] Subfolders(string root, string pattern)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return [];
        try { return Directory.GetDirectories(root, pattern); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }
}
