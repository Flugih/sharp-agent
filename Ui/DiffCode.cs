using System.Windows.Documents;

namespace SharpAgent.Ui;

internal static partial class DiffCode
{
    const string Keywords = "abstract|and|as|asc|async|await|begin|bool|boolean|break|by|case|catch|char|check|class|const|continue|create|def|default|delete|desc|else|end|enum|export|extends|false|finally|float|fn|for|foreach|from|func|function|go|group|if|implements|import|in|insert|int|interface|into|is|join|let|limit|long|match|namespace|new|nil|none|not|null|object|or|order|package|private|protected|public|raise|readonly|record|return|select|set|short|static|string|struct|switch|table|throw|true|try|type|typeof|union|update|use|using|var|void|where|while|with|yield";

    [GeneratedRegex("""(?<comment>//.*|/\*.*?(?:\*/|$))|(?<dash>--)|(?<hash>#)|(?<string>"(?:\\.|[^"\\])*"?|'(?:\\.|[^'\\])*'?|`[^`]*`?)|(?<number>\b(?:0[xX][\da-fA-F]+|\d+(?:\.\d+)?)\b)|(?<word>[\p{L}_][\p{L}\p{Nd}_]*)""")]
    private static partial Regex Tokens();

    [GeneratedRegex("""(?<comment><!--.*?(?:-->|$))|(?<tag></?[\p{L}_][\w:.-]*|<\?[\w:-]+|<![\w:-]+|/?>|\?>)|(?<name>[\w:.-]+)(?=\s*=)|(?<string>"[^"]*"?|'[^']*'?)""")]
    private static partial Regex MarkupTokens();

    static readonly HashSet<string> KeywordSet = new(Keywords.Split('|'), StringComparer.OrdinalIgnoreCase);

    internal static void Write(InlineCollection inlines, string text, string extension)
    {
        if (extension is "" or ".txt" or ".md" or ".log" || text.Length > 1200)
        {
            inlines.Add(new Run(text));
            return;
        }
        var tokens = extension is ".xaml" or ".xml" or ".csproj" or ".html" or ".xhtml" or ".svg" or ".props" or ".targets"
            ? MarkupTokens() : Tokens();
        int end = 0;
        foreach (Match token in tokens.Matches(text))
        {
            var value = token.Value;
            bool tailComment = token.Groups["dash"].Success && extension == ".sql" ||
                token.Groups["hash"].Success && extension is ".py" or ".ps1" or ".sh" or ".yaml" or ".yml" or ".toml";
            var brush = tailComment || token.Groups["comment"].Success ? "TextMuted"
                : token.Groups["string"].Success ? "CodeString"
                : token.Groups["number"].Success ? "CodeNumber"
                : token.Groups["tag"].Success || token.Groups["word"].Success && KeywordSet.Contains(value) ? "CodeKeyword"
                : token.Groups["name"].Success || token.Groups["word"].Success && (char.IsUpper(value[0]) || extension == ".sql") ? "CodeName" : null;
            if (brush == null) continue;
            if (end < token.Index) inlines.Add(new Run(text[end..token.Index]));
            inlines.Add(new Run(tailComment ? text[token.Index..] : value).Paint(TextElement.ForegroundProperty, brush));
            if (tailComment) return;
            end = token.Index + token.Length;
        }
        if (end < text.Length) inlines.Add(new Run(text[end..]));
    }
}
