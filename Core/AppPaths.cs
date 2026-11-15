namespace SharpAgent.Core;

public static class AppPaths
{
    public static string AppFolder { get; } = DetectAppFolder();

    public static string EnvPath => Path.Combine(AppFolder, DotEnv.FileName);
    public static string ChatGptAuthPath => Path.Combine(AppFolder, "auth", "chatgpt.json");
    public static string HistoryFolder => Path.Combine(AppFolder, "history");
    public static string LogsFolder => Path.Combine(HistoryFolder, "logs");
    public static string DefaultWorkspace => Path.Combine(AppFolder, "workspace");
    public static string AttachmentsFolder => Path.Combine(AppFolder, "attachments");
    public static string TempFolder => Path.Combine(AppFolder, "temp");
    public static string StatePath => Path.Combine(AppFolder, "state.json");
    public static string ErrorLogPath => Path.Combine(AppFolder, "errors.log");

    private static string DetectAppFolder()
    {
        var exeDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
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
        catch
        {
            return false;
        }
    }
}
