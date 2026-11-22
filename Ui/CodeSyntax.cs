using System.Windows.Documents;

namespace SharpAgent.Ui;

internal static partial class CodeSyntax
{
    const int BlockLimit = 24_000;
    const string Keywords = "abstract|and|as|asc|async|await|begin|bool|boolean|break|by|case|catch|char|check|class|const|continue|create|def|default|delete|desc|else|end|enum|export|extends|false|finally|float|fn|for|foreach|from|func|function|go|group|if|implements|import|in|insert|int|interface|into|is|join|let|limit|long|match|namespace|new|nil|none|not|null|object|or|order|package|private|protected|public|raise|readonly|record|return|select|set|short|static|string|struct|switch|table|throw|true|try|type|typeof|union|update|use|using|var|void|where|while|with|yield";

    [GeneratedRegex("""(?<comment>//[^\r\n]*|/\*[\s\S]*?(?:\*/|\z))|(?<dash>--)|(?<hash>#)|(?<string>"(?:\\.|[^"\\])*"?|'(?:\\.|[^'\\])*'?|`[^`]*`?)|(?<number>(?<!\w)-?(?:0[xX][\da-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\b)|(?<word>[\p{L}_][\p{L}\p{Nd}_]*)""")]
    private static partial Regex Tokens();

    [GeneratedRegex("""(?<comment><!--[\s\S]*?(?:-->|\z))|(?<tag></?[\p{L}_][\w:.-]*|<\?[\w:-]+|<![\w:-]+|/?>|\?>)|(?<name>[\w:.-]+)(?=\s*=)|(?<string>"[^"]*"?|'[^']*'?)""")]
    private static partial Regex MarkupTokens();

    static readonly HashSet<string> KeywordSet = new(Keywords.Split('|'), StringComparer.OrdinalIgnoreCase);

    internal static void WriteBlock(InlineCollection inlines, string text, string? language)
    {
        var extension = language?.ToLowerInvariant() switch
        {
            null or "" => IsJson(text) ? ".json" : "",
            "text" or "plain" or "plaintext" or "output" or "console" => "",
            "jsonc" => ".json",
            "python" => ".py",
            "powershell" or "pwsh" => ".ps1",
            "bash" or "shell" or "zsh" => ".sh",
            "javascript" => ".js",
            "typescript" => ".ts",
            var name => "." + name,
        };
        Write(inlines, text, extension, BlockLimit, "TextDim");
    }

    static bool IsJson(string text)
    {
        var start = text.AsSpan().TrimStart();
        if (text.Length > BlockLimit || start.IsEmpty || start[0] is not ('{' or '[')) return false;
        try
        {
            using var value = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static void Write(InlineCollection inlines, string text, string extension, int limit = 1200, string commentBrush = "TextMuted")
    {
        if (extension is "" or ".txt" or ".md" or ".log" || text.Length > limit)
        {
            inlines.Add(new Run(text));
            return;
        }
        var tokens = extension is ".xaml" or ".xml" or ".csproj" or ".html" or ".xhtml" or ".svg" or ".props" or ".targets"
            ? MarkupTokens() : Tokens();
        int end = 0;
        for (var token = tokens.Match(text); token.Success;)
        {
            var value = token.Value;
            int until = token.Index + token.Length;
            bool tailComment = token.Groups["dash"].Success && extension == ".sql" ||
                token.Groups["hash"].Success && extension is ".py" or ".ps1" or ".sh" or ".yaml" or ".yml" or ".toml";
            bool jsonKey = extension == ".json" && token.Groups["string"].Success &&
                text.AsSpan(until).TrimStart() is [':', ..];
            var brush = tailComment || token.Groups["comment"].Success ? commentBrush
                : token.Groups["string"].Success ? jsonKey ? "CodeName" : "CodeString"
                : token.Groups["number"].Success ? "CodeNumber"
                : token.Groups["tag"].Success || token.Groups["word"].Success && KeywordSet.Contains(value) ? "CodeKeyword"
                : token.Groups["name"].Success || token.Groups["word"].Success && (char.IsUpper(value[0]) || extension == ".sql") ? "CodeName" : null;
            if (brush != null)
            {
                if (end < token.Index) inlines.Add(new Run(text[end..token.Index]));
                if (tailComment) until = text.IndexOf('\n', until) is >= 0 and var newline ? newline : text.Length;
                inlines.Add(new Run(text[token.Index..until]).Paint(TextElement.ForegroundProperty, brush));
                end = until;
            }
            token = tailComment ? tokens.Match(text, until) : token.NextMatch();
        }
        if (end < text.Length) inlines.Add(new Run(text[end..]));
    }
}
