namespace SharpAgent.Agent;

static partial class AdaptiveModelRouter
{
    internal static bool IsSimple(string text) => text.Length is > 8 and <= 1500 &&
        Transformation().IsMatch(text.TrimStart()) && !text.Contains("://") && !text.Contains((char)96);

    [GeneratedRegex(@"^(?:переведи|перефразируй|исправь опечатки|кратко перескажи|резюмируй текст|выдели ключевые тезисы|translate|rephrase|fix typos|summarize|extract key points)(?: [^:\r\n]{1,35})?\s*:\s*\S", RegexOptions.IgnoreCase)]
    private static partial Regex Transformation();
}
