namespace SharpAgent.Core;

public static class AppPaths
{
    public static string InstalledModePath { get; } = Path.Combine(AppContext.BaseDirectory, "installed.mode");
    public static bool IsInstalled { get; } = File.Exists(InstalledModePath);
    public static string InstalledDataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpAgent");
    public static string AppFolder { get; } = DetectAppFolder();
    public static string EnvPath => In(DotEnv.FileName);
    public static string ChatGptAuthPath => Path.Combine(AppFolder, "auth", "chatgpt.json");
    public static string HistoryFolder => In("history");
    public static string LogsFolder => Path.Combine(HistoryFolder, "logs");
    public static string DefaultWorkspace => In("workspace");
    public static string AttachmentsFolder => In("attachments");
    public static string TempFolder => In("temp");
    public static string StatePath => In("state.json");
    public static string ErrorLogPath => In("errors.log");

    static string In(string name) => Path.Combine(AppFolder, name);

    static string DetectAppFolder()
    {
        var exeDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (IsInstalled) return InstalledDataFolder;
        if (File.Exists(Path.Combine(exeDir, DotEnv.FileName))) return exeDir;
        var dir = new DirectoryInfo(exeDir);
        while (dir != null && !dir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)) dir = dir.Parent;
        return dir?.Parent is { } project && project.EnumerateFiles("*.csproj").Any() ? project.FullName : exeDir;
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
