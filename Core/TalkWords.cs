namespace SharpAgent.Core;

static partial class TalkWords
{
    internal static string Normalize(string text) => Postgres().Replace(text, "PostgreSQL");

    static readonly string[] LatinSounds = ["а", "б", "к", "д", "е", "ф", "г", "х", "и", "дж", "к", "л", "м", "н", "о", "п", "к", "р", "с", "т", "у", "в", "в", "кс", "й", "з"];
    static readonly string[] LatinNames = ["эй", "би", "си", "ди", "и", "эф", "джи", "эйч", "ай", "джей", "кей", "эл", "эм", "эн", "оу", "пи", "кью", "ар", "эс", "ти", "ю", "ви", "дабл ю", "экс", "уай", "зи"];

    internal static string RussianPlaybackText(string text) =>
        RussianSpeechNumbers.Expand(LatinWord().Replace(Postgres().Replace(text, "Постгрес"), match =>
        {
            var word = match.Value.ToLowerInvariant();
            return match.Value.Length == 1 || match.Value.All(char.IsAsciiLetterUpper)
                ? string.Join(' ', word.Select(letter => LatinNames[letter - 'a']))
                : LatinSound().Replace(word, sound => sound.Value switch
                {
                    "ch" => "ч", "sh" => "ш", "th" => "т", "ph" => "ф", "ck" => "к", "oo" => "у", "ee" => "и",
                    _ => LatinSounds[sound.Value[0] - 'a'],
                });
        }));

    internal static bool IsStop(string text) => Stop().IsMatch(text);

    internal static bool IsTaskStop(string text) => TaskStop().IsMatch(text);

    internal static bool IsRecheck(string text) => Recheck().IsMatch(text);

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?:postgres(?:ql)?|пост[ -]?гр[еэ]сс?(?:[ -]?(?:эс[ -]?к[уью][ -]?эль|к[уью][ -]?эль|sql))?)(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Postgres();

    [GeneratedRegex("[A-Za-z]+")]
    private static partial Regex LatinWord();

    [GeneratedRegex("ch|sh|th|ph|ck|oo|ee|[a-z]")]
    private static partial Regex LatinSound();

    [GeneratedRegex(@"\A\s*(?:стоп|остановись|погоди|подожди|секунду|stop|wait|hold on|one second)[\s.!?,…]*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Stop();

    const string StopVerb = @"(?:останови|отмени|заверши|прерви|прекрати)(?:те)?";

    [GeneratedRegex(@"\A\s*(?:(?:слушай|ну|давай|пожалуйста|агент|please|(?:can|could) you)[\s,]+)*(?:" + StopVerb + @"(?:\s+или\s+" + StopVerb +
        @")?(?:\s+(?:выполнение\s+(?:(?:этой|текущей|активной)\s+)?задачи|(?:(?:эту|текущую|активную)\s+)?задачу(?:\s+(?:текущую|активную))?))?|(?:stop|cancel|end|abort|finish)\s+(?:(?:the|this)\s+)?(?:(?:current|active|running)\s+)?task)(?:[\s,]+(?:пожалуйста|please))?[\s.!?,…]*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaskStop();

    [GeneratedRegex(@"\A\s*(?:(?:смотри|слушай|ну|давай|пожалуйста|please)[\s,]+)*(?:(?:ещ[её]\s+раз\s+)?перепроверь(?:те)?|(?:ещ[её]\s+раз\s+проверь(?:те)?|проверь(?:те)?\s+(?:(?:эту|текущую|прошлую)\s+)?задачу\s+ещ[её]\s+раз|проверь(?:те)?\s+ещ[её]\s+раз)|recheck|check\s+(?:the\s+task\s+)?again)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Recheck();
}
