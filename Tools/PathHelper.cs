using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpAgent.Tools;

public static class PathHelper
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private const uint BackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, [Out] char[] longPath, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint size, uint flags);

    public static string Resolve(string? path, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) path = ".";
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ToolException(@"Пути вида \\?\... и \\.\... не поддерживаются — укажите обычный путь.");

        if (path == "~") path = Home;
        else if (path.StartsWith("~/") || path.StartsWith("~\\")) path = Path.Combine(Home, path[2..]);

        if (!Path.IsPathRooted(path)) path = Path.Combine(workingDirectory, path);
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException($"Некорректный путь: {path} ({ex.Message})");
        }

        if (full.IndexOf(':', 2) >= 0)
            throw new ToolException($"Некорректный путь: {full} (двоеточие допустимо только после буквы диска).");
        return ExpandShortNames(full);
    }

    private static string ExpandShortNames(string full) => full.Contains('~') ? MapExisting(full, LongName) : full;

    public static string FinalPath(string full) => MapExisting(full, FinalName);

    private static string MapExisting(string full, Func<string, string?> map)
    {
        try
        {
            var rest = "";
            for (var existing = full; !string.IsNullOrEmpty(existing); existing = Path.GetDirectoryName(existing))
            {
                if (Path.Exists(existing) && map(existing) is { } mapped) return Path.Combine(mapped, rest);
                rest = Path.Combine(Path.GetFileName(existing), rest);
            }
        }
        catch
        {
        }
        return full;
    }

    private static string? LongName(string path)
    {
        var buffer = new char[1024];
        uint length = GetLongPathName(path, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    private static string? FinalName(string path)
    {
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) return null;
        var final = new string(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..] : final[4..];
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);

    public static bool IsInside(string child, string parent)
    {
        var p = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        var c = Path.TrimEndingDirectorySeparator(child) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".msix", ".appx", ".scr", ".pif", ".lnk", ".url", ".reg", ".hta", ".cpl", ".jar",
        ".msc", ".scf", ".inf", ".application", ".appref-ms", ".py", ".pyw",
    };

    public static bool IsExecutable(string path) => ExecutableExtensions.Contains(Path.GetExtension(path));

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    public static string UniquePath(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (int n = 2; Path.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){extension}");
        return path;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} Б" : $"{size:0.#} {units[unit]}";
    }

    public static bool IsProtected(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (SamePath(Path.GetPathRoot(full) ?? "", full)) return true;

        var special = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        };
        var folders = special.Select(Environment.GetFolderPath)
            .Append(KnownFolders.Downloads)
            .Append(Path.Combine(Home, "OneDrive"))
            .Append(Environment.GetEnvironmentVariable("OneDrive") ?? "")
            .Append(Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "")
            .Append(Environment.GetEnvironmentVariable("OneDriveCommercial") ?? "");
        return folders.Any(folder => !string.IsNullOrEmpty(folder) && IsInside(folder, full));
    }

    private static readonly HashSet<string> PassiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".toml",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".epub", ".fb2", ".djvu",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".svg",
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".mp4", ".mkv", ".avi", ".mov", ".webm",
        ".zip", ".7z", ".rar", ".gz", ".tar",
        ".cs", ".csproj", ".sln", ".xaml", ".c", ".cpp", ".h", ".hpp", ".java", ".kt", ".go", ".rs", ".rb", ".php",
        ".ts", ".tsx", ".jsx", ".css", ".scss", ".sql", ".html", ".htm", ".tex", ".srt",
    };

    public static bool IsPassive(string path) => PassiveExtensions.Contains(Path.GetExtension(path));
}
