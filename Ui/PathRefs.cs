namespace SharpAgent.Ui;

public sealed class PathRefs(Func<string> workingDirectory)
{
    const int MaxPathLength = 400;
    const string Forbidden = "<>\"|?*\n\r\t`", TrailingPunctuation = ".,;:!?)]}»\"'… \u00A0";
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    readonly HashSet<char> _localDrives = [];

    public string? Resolve(string candidate, bool allowRelative)
    {
        var text = candidate.Trim().Trim('"', '\'').Trim();
        if (text.Length is 0 or > MaxPathLength || text.AsSpan().ContainsAny(Forbidden) || text is ['\\' or '/', '\\' or '/', ..]) return null;
        try
        {
            if (text.Contains('%')) text = Environment.ExpandEnvironmentVariables(text);
            if (text == "~") text = Home;
            else if (text is ['~', '/' or '\\', ..]) text = Path.Combine(Home, text[2..]);
            if (!(text is [var drive, ':', '\\' or '/', ..] && char.IsAsciiLetter(drive)))
            {
                if (!allowRelative || text.Contains(':') ||
                    !text.AsSpan().ContainsAny(@"\/") && !Regex.IsMatch(text, @"\.[A-Za-z0-9]{1,10}$")) return null;
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
        int length = Math.Min(text.Length - start, MaxPathLength), end = text.AsSpan(start, length).IndexOfAny(Forbidden);
        var candidate = text.Substring(start, end < 0 ? length : end);
        for (int guard = 0; guard < 64; guard++)
        {
            var trimmed = candidate.AsSpan().TrimEnd(TrailingPunctuation).ToString();
            if (trimmed.Length < 3) break;
            if (Resolve(trimmed, allowRelative: false) is { } full) return (trimmed.Length, full);
            int space = trimmed.AsSpan().LastIndexOfAny(' ', '\u00A0');
            if (space < 0) break;
            candidate = trimmed[..space];
        }
        return null;
    }

    public static int PlainTokenLength(string text, int start)
    {
        int end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && !Forbidden.Contains(text[end])) end++;
        return end - start;
    }

    bool IsOnLocalDrive(string fullPath)
    {
        if (fullPath is not [var letter, ':', _, ..]) return false;
        letter = char.ToUpperInvariant(letter);
        return _localDrives.Contains(letter) ||
               new DriveInfo(letter + @":\").DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram or DriveType.CDRom &&
               _localDrives.Add(letter);
    }
}
