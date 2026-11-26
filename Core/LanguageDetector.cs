namespace SharpAgent.Core;

public enum ReplyLanguage { English, Russian }

static class LanguageDetector
{
    static readonly Regex Words = new(@"(?s)```.*?(?:```|$)|`[^`]*`|\b(?:https?://|[A-Za-z]:[\\/])[^\s]+|\b[\p{L}\p{N}_-]+\.[A-Za-z0-9]{1,8}\b|(?<ru>[\u0400-\u052f]+)|(?<en>[A-Za-z]+)");
    static readonly Regex TranslationRequest = new(@"\A\s*(?:(?:пожалуйста|please|погоди|можешь)[,\s]+)*(?:перевед(?:и(?:те)?|ёшь)|перевести|произнеси(?:те)?|как\s+сказать|translate|pronounce|how\s+(?:do|would)\s+(?:i|you)\s+say)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly HashSet<string> VoiceFillers = new(StringComparer.OrdinalIgnoreCase)
        { "ok", "okay", "yes", "no", "yeah", "yep", "uh", "um", "hmm", "mm", "mhm", "ага", "угу", "мм", "хм" };
    static readonly Regex VoiceRequest = new("""
        \A \s* (?:
            (?:пожалуйста[,\s]+)?
            (?:(?:давай(?:те)?(?:\s+(?:говорить|общаться))?|говори(?:те)?|отвечай(?:те)?|ответь(?:те)?|переключись|перейд[её]м)[,\s]+(?:пожалуйста[,\s]+)?)?
            (?:по[-\s]+(?:(?<ru>русски)|(?<en>английски))|на\s+(?:(?<ru>русском|русский)|(?<en>английском|английский))(?:\s+язык(?:е)?)?)
          | (?:please\s+)? (?:let'?s\s+speak|switch\s+to|speak|answer|reply)(?:\s+in)?\s+(?:(?<ru>russian)|(?<en>english))
          | (?:(?<ru>russian)|(?<en>english))\s+please
        ) (?:[,\s]+(?:пожалуйста|please))? (?:\s*:[^\r\n]+)? [\s.!?,…]* \z
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

    internal static ReplyLanguage? Detect(string? text)
    {
        var (russian, english, first, hasEnglish) = CountWords(text);
        if (russian == 0) return hasEnglish ? ReplyLanguage.English : null;
        return russian == english ? first : russian > english ? ReplyLanguage.Russian : ReplyLanguage.English;
    }

    internal static ReplyLanguage? SpeechLanguageFor(string input, ReplyLanguage language) => TranslationRequest.IsMatch(input) ? null : language;

    internal static bool IsForeignReply(string text, ReplyLanguage language)
    {
        var (russian, english, _, hasEnglish) = CountWords(text);
        return language == ReplyLanguage.Russian
            ? hasEnglish && (russian == 0 || english >= 4 && english >= russian * 2)
            : russian > 0 && (english == 0 || russian >= 4 && russian >= english * 2);
    }

    static (int Russian, int English, ReplyLanguage? First, bool HasEnglish) CountWords(string? text, bool voice = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        int russian = 0, english = 0;
        ReplyLanguage? first = null;
        bool hasEnglish = false;
        foreach (Match word in Words.Matches(text))
        {
            if (voice && VoiceFillers.Contains(word.Value)) continue;
            if (word.Groups["ru"].Success)
            {
                russian++;
                first ??= ReplyLanguage.Russian;
            }
            if (!word.Groups["en"].Success) continue;
            hasEnglish = true;
            if (word.Value.Skip(1).Any(char.IsUpper)) continue;
            english++;
            first ??= ReplyLanguage.English;
        }
        return (russian, english, first, hasEnglish);
    }

    internal sealed class Conversation(ReplyLanguage fallback)
    {
        ReplyLanguage? _current, _candidate;

        internal ReplyLanguage Resolve(string text, bool typed = false)
        {
            if (VoiceRequest.Match(text) is { Success: true } request)
                return Set(request.Groups["ru"].Success ? ReplyLanguage.Russian : ReplyLanguage.English);
            var (russian, english, _, _) = CountWords(text, voice: true);
            var active = _current ?? fallback;
            if (russian >= 3 && russian >= english) return typed ? ReplyLanguage.Russian : Set(ReplyLanguage.Russian);
            if (russian + english == 0)
            {
                if (!typed) _candidate = null;
                return active;
            }
            var detected = russian > 0 ? ReplyLanguage.Russian : ReplyLanguage.English;
            bool clear = (russian == 0 || english == 0) && russian + english >= 3;
            if (typed) return _current == null || clear ? detected : active;
            if (_current == null || detected == active || clear && _candidate == detected) return Set(detected);
            _candidate = clear ? detected : null;
            return active;
        }

        ReplyLanguage Set(ReplyLanguage language)
        {
            _candidate = null;
            _current = language;
            return language;
        }
    }

    internal static string Guidance(ReplyLanguage language) =>
        $"Honor explicit language requests. Otherwise reply in {language}.";
}
