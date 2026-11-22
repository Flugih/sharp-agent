namespace SharpAgent.Core;

static class AppCache
{
    internal const long CacheLimit = 192L * 1024 * 1024, TempLimit = 256L * 1024 * 1024, LogLimit = 128L * 1024 * 1024;
    static int _running;

    internal static void Maintain()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            Prune(AppPaths.CacheFolder, CacheLimit, TimeSpan.FromDays(30));
            Prune(AppPaths.TempFolder, TempLimit, TimeSpan.FromDays(2));
            Prune(AppPaths.LogsFolder, LogLimit, TimeSpan.FromDays(CommandLog.KeepDays), toolsOnly: true);
            ProjectIndexStore.Trim(AppPaths.DatabaseFolder, CacheLimit);
        }
        finally { Volatile.Write(ref _running, 0); }
    }

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
