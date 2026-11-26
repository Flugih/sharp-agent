namespace SharpAgent.Core;

static partial class RussianSpeechNumbers
{
    static readonly string[] Units = ["ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять",
        "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"];
    static readonly string[] Tens = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];
    static readonly string[] Hundreds = ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"];
    static readonly (string One, string Few, string Many)[] Scales = [default, ("тысяча", "тысячи", "тысяч"),
        ("миллион", "миллиона", "миллионов"), ("миллиард", "миллиарда", "миллиардов"), ("триллион", "триллиона", "триллионов"),
        ("квадриллион", "квадриллиона", "квадриллионов"), ("квинтиллион", "квинтиллиона", "квинтиллионов")];

    internal static string Expand(string text) => Number().Replace(text, match =>
    {
        var digits = string.Concat(match.Groups["whole"].Value.Where(char.IsAsciiDigit));
        var words = new List<string>();
        if (match.Groups["sign"].Success) words.Add(match.Groups["sign"].Value == "+" ? "плюс" : "минус");
        words.Add(ReadNumber(digits));
        var parts = match.Groups["part"].Captures;
        foreach (Capture part in parts)
        {
            words.Add(part.Value[0] switch { '.' => "точка", ',' => "запятая", ':' => "двоеточие", _ => "дробь" });
            words.Add(ReadNumber(part.Value[1..]));
        }
        if (match.Groups["percent"].Success)
            words.Add(parts.Count == 0 ? Form(int.Parse(digits.AsSpan(Math.Max(0, digits.Length - 2))), ("процент", "процента", "процентов")) : "процента");
        return " " + string.Join(' ', words) + " ";
    });

    static string ReadNumber(string digits) => (digits.Length == 1 || digits[0] != '0') && ulong.TryParse(digits, out var value)
        ? Cardinal(value) : string.Join(' ', digits.Select(digit => Units[digit - '0']));

    static string Cardinal(ulong value)
    {
        if (value == 0) return Units[0];
        var groups = new Stack<string>();
        for (int scale = 0; value > 0; value /= 1000, scale++)
        {
            int group = (int)(value % 1000);
            if (group == 0) continue;
            var words = new List<string>();
            if (group >= 100) words.Add(Hundreds[group / 100]);
            int rest = group % 100;
            if (rest >= 20)
            {
                words.Add(Tens[rest / 10]);
                rest %= 10;
            }
            if (rest > 0) words.Add((scale, rest) switch { (1, 1) => "одна", (1, 2) => "две", _ => Units[rest] });
            if (scale > 0) words.Add(Form(group, Scales[scale]));
            groups.Push(string.Join(' ', words));
        }
        return string.Join(' ', groups);
    }

    static string Form(int number, (string One, string Few, string Many) forms) => number % 100 is >= 11 and <= 14 ? forms.Many
        : (number % 10) switch { 1 => forms.One, 2 or 3 or 4 => forms.Few, _ => forms.Many };

    [GeneratedRegex(@"(?<sign>(?<![\p{L}\p{N}])[+\-−])?(?<whole>[0-9]+(?:[ \u00a0\u202f][0-9]{3}\b)*)(?<part>[.,:/][0-9]+)*(?<percent>[ \u00a0\u202f]*%)?")]
    private static partial Regex Number();
}
