namespace SharpAgent.Core;

public static class AppPaths
{
    public static string InstalledModePath { get; } = Path.Combine(AppContext.BaseDirectory, "installed.mode");
    internal static string PortableModePath { get; } = Path.Combine(AppContext.BaseDirectory, "portable.mode");
    public static string InstalledDataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpAgent");
    internal static string LegacyFolder { get; } = DetectAppFolder();
    public static bool IsInstalled => PathHelper.SamePath(LegacyFolder, InstalledDataFolder);
    internal static bool ProtectSecrets => !PathHelper.SamePath(LegacyFolder, AppContext.BaseDirectory);
    public static string AppFolder { get; } = Path.Combine(LegacyFolder, "data");
    public static string EnvPath => In(DotEnv.FileName);
    public static string ChatGptAuthPath => Path.Combine(AppFolder, "auth", "chatgpt.json");
    public static string HistoryFolder => In(@"history\chats");
    public static string LogsFolder => In(@"logs\tools");
    internal static string CheckpointsFor(string logsFolder) => PathHelper.SamePath(logsFolder, LogsFolder)
        ? In("checkpoints") : Path.GetFullPath(Path.Combine(logsFolder, "..", "changes"));
    internal static string BrowserFolder => In(@"auth\browser");
    public static string DefaultWorkspace => In("workspace");
    public static string AttachmentsFolder => In("attachments");
    public static string TempFolder => In("temp");
    internal static string CacheFolder => In("cache");
    internal static string DatabaseFolder => In("database");
    public static string StatePath => In("state.json");
    public static string ErrorLogPath => In(@"logs\errors\errors.log");

    static string In(string name) => Path.Combine(AppFolder, name);

    static string DetectAppFolder()
    {
        var exeDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (File.Exists(InstalledModePath)) return InstalledDataFolder;
        if (File.Exists(PortableModePath) || File.Exists(Path.Combine(exeDir, DotEnv.FileName))) return exeDir;
        var dir = new DirectoryInfo(exeDir);
        while (dir != null && !dir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)) dir = dir.Parent;
        return dir?.Parent is { } project && project.EnumerateFiles("*.csproj").Any() ? project.FullName : InstalledDataFolder;
    }

    static readonly (string Old, string New)[] MigratedPaths =
    [
        (".env", ".env"), ("state.json", "state.json"), ("auth", "auth"), ("Browser", @"auth\browser"),
        (@"history\logs", @"logs\tools"), (@"history\changes", "checkpoints"), ("history", @"history\chats"),
        ("workspace", "workspace"), ("attachments", "attachments"), ("plugins", "plugins"), ("errors.log", @"logs\errors\errors.log"),
    ];

    internal static string Relocate(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        foreach (var (old, target) in MigratedPaths)
        {
            var source = Path.Combine(LegacyFolder, old);
            if (!PathHelper.IsInside(path, source)) continue;
            try { return Path.GetFullPath(Path.Combine(In(target), Path.GetRelativePath(source, path))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return path; }
        }
        return path;
    }

    internal static void Migrate(string legacy, string destination, bool protect)
    {
        if (Directory.Exists(destination))
        {
            var completed = Path.Combine(destination, ".migration-version");
            if (File.Exists(completed))
            {
                if ((File.GetAttributes(completed) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(completed) != "1")
                    throw new IOException("Версия завершённой миграции повреждена: " + completed);
                return;
            }
            // Do not silently accept a partial target while legacy data still exists.
            if (MigratedPaths.Any(item => File.Exists(Path.Combine(legacy, item.Old)) ||
                                           Directory.Exists(Path.Combine(legacy, item.Old))))
                throw new IOException("Папка данных существует без отметки завершённой миграции. " +
                    "Исходные данные сохранены; проверьте папку вручную: " + destination);
            return;
        }
        var stage = destination + ".migrating";
        var marker = Path.Combine(stage, ".migration-version");
        if (Directory.Exists(stage))
        {
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0 ||
                (File.Exists(marker) ? (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(marker) != "1" :
                    Directory.EnumerateFileSystemEntries(stage).Any()))
                throw new IOException("Папка миграции уже занята: " + stage);
            Directory.Delete(stage, true);
        }
        Directory.CreateDirectory(stage);
        File.WriteAllText(marker, "1");
        foreach (var (old, target) in MigratedPaths)
        {
            var copy = Path.Combine(stage, target);
            if (old == "Browser" && Directory.Exists(copy)) continue;
            Copy(Path.Combine(legacy, old), copy, old == "history");
        }
        if (protect)
        {
            var env = Path.Combine(stage, DotEnv.FileName);
            if (File.Exists(env)) DotEnv.ProtectExisting(env);
            var auth = Path.Combine(stage, "auth", "chatgpt.json");
            if (File.Exists(auth)) File.WriteAllText(auth, ProtectAuth(ReadAuth(File.ReadAllText(auth))));
        }
        Directory.Move(stage, destination);
    }

    internal static string ProtectAuth(string json) => new JsonObject { ["dpapi"] = UserDataProtection.Protect(json) }.ToJsonString();
    internal static string ReadAuth(string json) => JsonNode.Parse(json) is JsonObject obj && Str(obj["dpapi"]) is { } text
        ? UserDataProtection.Read(text) : json;

    internal static string MigrationFolder => AppFolder + ".migrating";

    static void Copy(string source, string target, bool history = false)
    {
        if (!File.Exists(source) && !Directory.Exists(source)) return;
        var attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (File.Exists(target) || Directory.Exists(target)) &&
            (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Миграция данных не следует по ссылкам: " + source);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.CreateDirectory(target);
            foreach (var item in Directory.EnumerateFileSystemEntries(source))
            {
                var name = Path.GetFileName(item);
                if (history && (name.Equals("logs", StringComparison.OrdinalIgnoreCase) || name.Equals("changes", StringComparison.OrdinalIgnoreCase))) continue;
                Copy(item, Path.Combine(target, name));
            }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var input = File.OpenRead(source);
        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            output.Flush(true);
        }
        input.Position = 0;
        using (var verified = File.OpenRead(target))
            if (!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(verified))) throw new IOException("Не удалось проверить копию: " + source);
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
    }

    public static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
