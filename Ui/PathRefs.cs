using System.Text.RegularExpressions;

namespace SharpAgent.Ui;

public sealed class PathRefs(Func<string> workingDirectory)
{
    private const int MaxPathLength = 400;
    private static readonly char[] Forbidden = ['<', '>', '"', '|', '?', '*', '\n', '\r', '\t', '`'];
    private static readonly char[] TrailingPunctuation =
        ['.', ',', ';', ':', '!', '?', ')', ']', '}', '»', '"', '\'', '…', ' ', '\u00A0'];
    private static readonly Regex DriveStartRx = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);
    private static readonly Regex ExtensionRx = new(@"\.[A-Za-z0-9]{1,10}$", RegexOptions.Compiled);
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private readonly HashSet<char> _localDrives = new();

    public string? Resolve(string? candidate, bool allowRelative)
    {
        var text = candidate?.Trim().Trim('"', '\'').Trim() ?? "";
        if (text.Length is 0 or > MaxPathLength || text.IndexOfAny(Forbidden) >= 0) return null;
        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal)) return null;

        try
        {
            if (text.Contains('%')) text = Environment.ExpandEnvironmentVariables(text);
            if (text == "~") text = Home;
            else if (text is ['~', '/' or '\\', ..]) text = Path.Combine(Home, text[2..]);

            if (!DriveStartRx.IsMatch(text))
            {
                if (!allowRelative || text.Contains(':') || !LooksLikeRelativePath(text)) return null;
                text = Path.Combine(workingDirectory(), text);
            }

            var full = Path.GetFullPath(text);
            return IsOnLocalDrive(full) && Path.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    public (int Length, string FullPath)? MatchBarePath(string text, int start)
    {
        int end = text.IndexOfAny(Forbidden, start);
        if (end < 0) end = text.Length;
        end = Math.Min(end, start + MaxPathLength);
        var candidate = text[start..end];

        for (int guard = 0; guard < 64; guard++)
        {
            var trimmed = candidate.TrimEnd(TrailingPunctuation);
            if (trimmed.Length < 3) break;
            if (Resolve(trimmed, allowRelative: false) is { } full) return (trimmed.Length, full);

            int space = trimmed.LastIndexOfAny([' ', '\u00A0']);
            if (space < 0) break;
            candidate = trimmed[..space];
        }
        return null;
    }

    public static int PlainTokenLength(string text, int start)
    {
        int end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && Array.IndexOf(Forbidden, text[end]) < 0) end++;
        return end - start;
    }

    private static bool LooksLikeRelativePath(string text) =>
        text.Contains('\\') || text.Contains('/') || ExtensionRx.IsMatch(text);

    private bool IsOnLocalDrive(string fullPath)
    {
        if (fullPath.Length < 3 || fullPath[1] != ':') return false;
        char letter = char.ToUpperInvariant(fullPath[0]);
        if (_localDrives.Contains(letter)) return true;
        bool local = new DriveInfo(letter + @":\").DriveType
            is DriveType.Fixed or DriveType.Removable or DriveType.Ram or DriveType.CDRom;
        if (local) _localDrives.Add(letter);
        return local;
    }
}
