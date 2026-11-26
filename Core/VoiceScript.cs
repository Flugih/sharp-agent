namespace SharpAgent.Core;

enum VoiceCue { Waiting, Retry, Unavailable, Error }
enum VoiceQuestion { None, Task, Plan, Step, RunningCommand, LastCommand }

sealed class VoiceScript
{
    sealed record Script((Regex Pattern, string[] Lines, VoiceQuestion Question)[] Replies, Dictionary<VoiceCue, string[]> Cues);

    sealed class Variants(string[] lines)
    {
        readonly string[] _order = lines.Distinct(StringComparer.Ordinal).ToArray();
        int _next = int.MaxValue;

        internal string Pick()
        {
            if (_next >= _order.Length)
            {
                var previous = _next == _order.Length ? _order[^1] : null;
                Random.Shared.Shuffle(_order);
                if (_order.Length > 1 && _order[0] == previous)
                {
                    int other = Random.Shared.Next(1, _order.Length);
                    (_order[0], _order[other]) = (_order[other], _order[0]);
                }
                _next = 0;
            }
            return _order[_next++];
        }
    }

    static readonly Script Russian = Load("ru"), English = Load("en");
    readonly Dictionary<string[], Variants> _variants = [];

    internal (string Line, bool Repeat, VoiceQuestion Question)? Reply(string text, ReplyLanguage language)
    {
        var normalized = text.TrimStart().TrimEnd(' ', '\t', '\r', '\n', '.', '!', '?', ',', '…').Replace('’', '\'');
        try
        {
            foreach (var (pattern, lines, question) in For(language).Replies)
                if (pattern.Match(normalized) is { Success: true } match) return (Pick(lines), match.Groups["repeat"].Success, question);
        }
        catch (RegexMatchTimeoutException) { }
        return null;
    }

    internal string Line(VoiceCue cue, ReplyLanguage language) => Pick(For(language).Cues[cue]);

    string Pick(string[] lines)
    {
        if (!_variants.TryGetValue(lines, out var variants)) _variants[lines] = variants = new(lines);
        return variants.Pick();
    }

    static Script For(ReplyLanguage language) => language == ReplyLanguage.Russian ? Russian : English;

    static Script Load(string language) => new(
        Read($"script.{language}.txt").Select(entry =>
        {
            var pattern = new Regex($"\\A(?:{entry.Key})\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            var question = pattern.GetGroupNames().Select(name => Enum.TryParse<VoiceQuestion>(name, out var value) ? value : VoiceQuestion.None)
                .FirstOrDefault(value => value != VoiceQuestion.None);
            return (pattern, entry.Lines, question);
        }).ToArray(),
        Read($"reasoning.{language}.txt").ToDictionary(entry => Enum.Parse<VoiceCue>(entry.Key, ignoreCase: true), entry => entry.Lines));

    static IEnumerable<(string Key, string[] Lines)> Read(string name)
    {
        using var stream = typeof(VoiceScript).Assembly.GetManifestResourceStream("SharpAgent.Voice." + name)
            ?? throw new InvalidOperationException($"Missing voice script: {name}");
        using var reader = new StreamReader(stream);
        List<string> lines = [];
        string? key = null;
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith('@'))
            {
                lines.Add(line);
                continue;
            }
            if (key != null && lines.Count > 0) yield return (key, lines.ToArray());
            key = line[1..];
            lines.Clear();
        }
        if (key != null && lines.Count > 0) yield return (key, lines.ToArray());
    }
}
