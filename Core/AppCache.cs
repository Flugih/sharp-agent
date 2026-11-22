namespace SharpAgent.Core;

static class AppCache
{
    internal const long CacheLimit = 192L * 1024 * 1024, TempLimit = 256L * 1024 * 1024, LogLimit = 128L * 1024 * 1024;
    internal const string VerificationMarker = ".verification.lock";
    static int _running;

    internal static void Maintain()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            CleanupVerificationArtifacts(AppPaths.LegacyFolder, RecycleBin.Send);
            Prune(AppPaths.CacheFolder, CacheLimit, TimeSpan.FromDays(30));
            Prune(AppPaths.TempFolder, TempLimit, TimeSpan.FromDays(2));
            Prune(AppPaths.LogsFolder, LogLimit, TimeSpan.FromDays(CommandLog.KeepDays), toolsOnly: true);
            ProjectIndexStore.Trim(AppPaths.DatabaseFolder, CacheLimit);
            LocalResearchEngine.Trim(AppPaths.DatabaseFolder, CacheLimit);
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    internal static void CleanupVerificationArtifacts(string project, Action<string> recycle, DateTime? nowUtc = null)
    {
        if (!File.Exists(Path.Combine(project, "SharpAgent.csproj"))) return;
        var scratch = new DirectoryInfo(Path.Combine(project, ".sharpagent"));
        var root = new DirectoryInfo(Path.Combine(scratch.FullName, "tmp"));
        var cutoff = (nowUtc ?? DateTime.UtcNow) - TimeSpan.FromMinutes(10);
        try
        {
            if (!LocalDirectory(scratch) || !LocalDirectory(root)) return;
            foreach (var run in root.EnumerateDirectories())
            {
                try
                {
                    if (!Guid.TryParseExact(run.Name, "N", out _) || !LocalDirectory(run)) continue;
                    var marker = new FileInfo(Path.Combine(run.FullName, VerificationMarker));
                    if (!marker.Exists || marker.LastWriteTimeUtc > cutoff || (marker.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    using (var lease = marker.Open(FileMode.Open, FileAccess.Read, FileShare.None))
                    using (var reader = new StreamReader(lease, Encoding.ASCII))
                        if (lease.Length != run.Name.Length || reader.ReadToEnd() != run.Name || ContainsRecentEntries(run)) continue;
                    if (LocalDirectory(run)) recycle(run.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        bool ContainsRecentEntries(DirectoryInfo directory) => directory.EnumerateFileSystemInfos().Any(entry =>
            (entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LastWriteTimeUtc > cutoff ||
            entry is DirectoryInfo child && ContainsRecentEntries(child));
    }

    static bool LocalDirectory(DirectoryInfo directory) => directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
        PathHelper.SamePath(directory.FullName, PathHelper.FinalPath(directory.FullName));

    internal static void Prune(string root, long bytes, TimeSpan retention, bool toolsOnly = false, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var files = new List<FileInfo>();
        var dirs = new Stack<string>([root]);
        var scanned = new List<string>();
        while (dirs.TryPop(out var path))
        {
            try
            {
                var directory = new DirectoryInfo(path);
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !PathHelper.SamePath(path, PathHelper.FinalPath(path))) continue;
                scanned.Add(path);
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (!toolsOnly && entry is DirectoryInfo child) dirs.Push(child.FullName);
                    if (entry is FileInfo file && (!toolsOnly || file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                        file.Name.StartsWith("screen_", StringComparison.OrdinalIgnoreCase))) files.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        long total = 0;
        var entries = new List<(FileInfo File, long Size, DateTime Written)>();
        foreach (var file in files)
        {
            try
            {
                long size = file.Length;
                total += size;
                entries.Add((file, size, file.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var (file, size, written) in entries.OrderBy(entry => entry.Written))
        {
            if (now - written <= TimeSpan.FromMinutes(10) || (total <= bytes && now - written <= retention)) continue;
            try
            {
                file.Refresh();
                if (!file.Exists || file.LastWriteTimeUtc != written || file.Length != size ||
                    !PathHelper.SamePath(file.FullName, PathHelper.FinalPath(file.FullName))) continue;
                file.Delete();
                total -= size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var directory in scanned.Skip(1).Reverse())
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any() && PathHelper.SamePath(directory, PathHelper.FinalPath(directory)))
                    Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
