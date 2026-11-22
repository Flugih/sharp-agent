using Microsoft.Win32.SafeHandles;
using Folder = System.Environment.SpecialFolder;

namespace SharpAgent.Tools;

public static class PathHelper
{
    const uint BackupSemantics = 0x02000000;
    static readonly string Home = Environment.GetFolderPath(Folder.UserProfile);
    static readonly HashSet<string> Executable = Extensions(".exe .bat .cmd .com .ps1 .psm1 .vbs .vbe .js .jse .wsf .wsh .msi .msp .msix .appx .scr .pif .lnk .url .reg .hta .cpl .jar .msc .scf .inf .application .appref-ms .py .pyw .dll");
    static readonly HashSet<string> Passive = Extensions(".txt .md .log .csv .tsv .json .xml .yaml .yml .ini .cfg .conf .toml .pdf .doc .docx .xls .xlsx .ppt .pptx .odt .ods .odp .rtf .epub .fb2 .djvu .png .jpg .jpeg .gif .bmp .webp .tif .tiff .ico .heic .svg .mp3 .wav .flac .ogg .m4a .mp4 .mkv .avi .mov .webm .zip .7z .rar .gz .tar .cs .xaml .c .cpp .h .hpp .java .kt .go .rs .php .ts .tsx .jsx .css .scss .sql .html .htm .tex .srt");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetLongPathName(string shortPath, [Out] char[] longPath, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint size, uint flags);
    [DllImport("kernel32.dll")] static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out FileBasicInfo info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    struct FileBasicInfo { public long Created, Accessed, Written, Changed; public uint Attributes; }

    internal static long Revision(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, 0, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()) ? info.Changed : 0;

    internal static long Revision(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Revision(handle);
    }

    static HashSet<string> Extensions(string list) => new(list.Split(' '), StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string? path, string workingDirectory)
    {
        path = Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(path) ? "." : path.Trim().Trim('"'));
        if (path == "~") path = Home;
        else if (path.StartsWith("~/") || path.StartsWith("~\\")) path = Path.Combine(Home, path[2..]);
        if (!Path.IsPathRooted(path)) path = Path.Combine(workingDirectory, path);
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException($"Некорректный путь: {path} ({ex.Message})");
        }
        if (full is ['\\', '\\' or '?', '?' or '.', '\\', ..])
            throw new ToolException(@"Пути вида \\?\... и \\.\... не поддерживаются — укажите обычный путь.");
        if (full.IndexOf(':', 2) >= 0)
            throw new ToolException($"Некорректный путь: {full} (двоеточие допустимо только после буквы диска).");
        return full.Contains('~') && !full.StartsWith(@"\\") ? MapExisting(full, LongName) : full;
    }

    public static string FinalPath(string full) => MapExisting(full, FinalName);

    static string MapExisting(string full, Func<string, string?> map)
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
        catch { }
        return full;
    }

    static string? LongName(string path)
    {
        var buffer = new char[1024];
        return Text(buffer, GetLongPathName(path, buffer, 1024));
    }

    static string? FinalName(string path)
    {
        using var handle = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024];
        return Text(buffer, GetFinalPathNameByHandle(handle, buffer, 1024, 0)) is not { } final ? null
            : final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..] : final[4..];
    }

    static string? Text(char[] buffer, uint length) => length == 0 || length >= buffer.Length ? null : new(buffer, 0, (int)length);

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsInside(string child, string parent)
    {
        var p = Path.TrimEndingDirectorySeparator(parent);
        if (!Path.EndsInDirectorySeparator(p)) p += Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(child) + Path.DirectorySeparatorChar).StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExecutable(string path) => Executable.Contains(Path.GetExtension(path));
    public static bool IsPassive(string path) => Passive.Contains(Path.GetExtension(path));

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    public static string UniquePath(string path)
    {
        var (folder, stem, extension) = (Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
        for (int n = 2; Path.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){extension}");
        return path;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = bytes;
        int unit = 0;
        for (; size >= 1024 && unit < units.Length - 1; unit++) size /= 1024;
        return unit == 0 ? $"{bytes} Б" : $"{size:0.#} {units[unit]}";
    }

    public static bool IsProtected(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (SamePath(Path.GetPathRoot(full) ?? "", full)) return true;
        Folder[] special = [Folder.UserProfile, Folder.Desktop, Folder.DesktopDirectory, Folder.MyDocuments, Folder.MyPictures, Folder.MyMusic,
            Folder.MyVideos, Folder.ApplicationData, Folder.LocalApplicationData, Folder.CommonApplicationData, Folder.Windows,
            Folder.ProgramFiles, Folder.ProgramFilesX86];
        return special.Select(Environment.GetFolderPath)
            .Concat([KnownFolders.Downloads, Path.Combine(Home, "OneDrive")])
            .Concat(new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(name => Environment.GetEnvironmentVariable(name) ?? ""))
            .Any(folder => !string.IsNullOrEmpty(folder) && IsInside(folder, full));
    }
}
