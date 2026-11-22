using System.IO.Compression;

namespace SharpAgent.Tools;

static partial class PdfText
{
    internal static LocalResearchEngine.Document Extract(string path, int limit, CancellationToken ct)
    {
        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        if (!raw.StartsWith("%PDF-", StringComparison.Ordinal)) throw new ToolException(SharpAgent.Ui.UiLanguage.Tr("Повреждённый PDF.", "Invalid PDF."));
        var objects = Object().Matches(raw).Cast<Match>().Take(25_000)
            .GroupBy(match => match.Groups["id"].Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Groups["body"].Value, StringComparer.Ordinal);
        var catalog = objects.Values.FirstOrDefault(body => Regex.IsMatch(body, @"/Type\s*/Catalog\b"));
        var pages = new List<string>();
        var root = catalog == null ? "" : Regex.Match(catalog, @"/Pages\s+(\d+)\s+\d+\s+R").Groups[1].Value;
        var seen = new HashSet<string>();
        void Visit(string id, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 40 || pages.Count >= 400 || !seen.Add(id) || !objects.TryGetValue(id, out var body)) return;
            if (Page().IsMatch(body)) { pages.Add(body); return; }
            var kids = Regex.Match(body, @"/Kids\s*\[([^\]]*)\]");
            foreach (Match child in Reference().Matches(kids.Groups[1].Value)) Visit(child.Groups[1].Value, depth + 1);
        }
        if (root.Length > 0) Visit(root, 0);
        bool complete = pages.Count > 0 && pages.Count < 400 && !raw.Contains("/Encrypt", StringComparison.Ordinal);
        if (pages.Count == 0) pages.AddRange(objects.Values.Where(body => Page().IsMatch(body)).Take(400));
        var fonts = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (id, body) in objects)
        {
            ct.ThrowIfCancellationRequested();
            var unicode = Regex.Match(body, @"/ToUnicode\s+(\d+)\s+\d+\s+R");
            if (unicode.Success && objects.TryGetValue(unicode.Groups[1].Value, out var cmap) && Stream(cmap, ct) is { } text)
                fonts[id] = UnicodeMap(text);
        }
        var output = new StringBuilder();
        for (int i = 0; i < pages.Count && output.Length < limit; i++)
        {
            var page = pages[i];
            var resource = Regex.Match(page, @"/Resources\s+(\d+)\s+\d+\s+R");
            var resourceBody = resource.Success ? objects.GetValueOrDefault(resource.Groups[1].Value) ?? page : page;
            var fontRefs = Regex.Matches(resourceBody, @"/(?<name>[^\s/<>]+)\s+(?<id>\d+)\s+\d+\s+R").Cast<Match>()
                .Where(match => fonts.ContainsKey(match.Groups["id"].Value))
                .GroupBy(match => match.Groups["name"].Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => fonts[group.First().Groups["id"].Value], StringComparer.Ordinal);
            var contents = Contents().Match(page);
            var content = new StringBuilder();
            foreach (Match link in Reference().Matches(contents.Groups["refs"].Value))
            {
                ct.ThrowIfCancellationRequested();
                if (!objects.TryGetValue(link.Groups[1].Value, out var obj) || Stream(obj, ct) is not { } stream) { complete = false; continue; }
                Dictionary<string, string>? map = null;
                foreach (Match operation in TextOperation().Matches(stream))
                {
                    if (operation.Groups["font"].Success) { fontRefs.TryGetValue(operation.Groups["font"].Value, out map); continue; }
                    foreach (Match token in TextToken().Matches(operation.Groups["text"].Value))
                    {
                        var id = token.Groups["hex"].Success ? token.Groups["hex"].Value.Replace(" ", "") : null;
                        var text = id == null ? Literal(token.Groups["literal"].Value) : Hex(id, map);
                        if (id == null && map is { Count: > 0 }) text = Hex(Convert.ToHexString(Encoding.Latin1.GetBytes(text)), map);
                        content.Append(text);
                        if (content.Length >= limit) break;
                    }
                    if (content.Length >= limit) break;
                    content.Append('\n');
                }
                if (stream.Contains("/Image", StringComparison.Ordinal) || Regex.IsMatch(stream, @"\sDo\b")) complete = false;
            }
            if (content.Length == 0) { complete = false; continue; }
            output.Append("=== PDF ").Append(i + 1).Append(" ===\n").Append(content);
        }
        if (output.Length >= limit || raw.Contains("/ObjStm", StringComparison.Ordinal)) complete = false;
        return new(output.ToString(0, Math.Min(output.Length, limit)), complete ? "ready" : "partial");
    }

    static string? Stream(string body, CancellationToken ct)
    {
        var match = PdfStream().Match(body);
        if (!match.Success) return null;
        var dictionary = match.Groups["dict"].Value;
        var data = match.Groups["data"].Value;
        var length = Regex.Match(dictionary, @"/Length\s+(\d+)\b(?!\s+\d+\s+R)");
        if (length.Success && int.TryParse(length.Groups[1].Value, out int len) && len <= data.Length) data = data[..len];
        if (!dictionary.Contains("/Filter", StringComparison.Ordinal)) return data;
        if (!Regex.IsMatch(dictionary, @"/Filter\s*(?:/FlateDecode\b|\[\s*/FlateDecode\s*\])")) return null;
        try
        {
            using var zip = new ZLibStream(new MemoryStream(Encoding.Latin1.GetBytes(data)), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16_384];
            int read;
            while ((read = zip.Read(buffer)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (output.Length + read > 2_000_000) return null;
                output.Write(buffer, 0, read);
            }
            return Encoding.Latin1.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (InvalidDataException) { return null; }
    }

    static Dictionary<string, string> UnicodeMap(string cmap)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in Regex.Matches(cmap, @"(?s)beginbfchar(.*?)endbfchar"))
            foreach (Match pair in Regex.Matches(block.Groups[1].Value, @"<([0-9a-fA-F]+)>\s*<([0-9a-fA-F]+)>").Cast<Match>().Take(8_000))
                result[pair.Groups[1].Value] = Hex(pair.Groups[2].Value, null, unicode: true);
        foreach (Match block in Regex.Matches(cmap, @"(?s)beginbfrange(.*?)endbfrange"))
            foreach (Match range in Regex.Matches(block.Groups[1].Value, @"<([0-9a-fA-F]{2,4})>\s*<([0-9a-fA-F]{2,4})>\s*(?:<([0-9a-fA-F]{4})>|\[([^\]]*)\])").Cast<Match>().Take(2_000))
            {
                var code = range.Groups[1].Value;
                int start = Convert.ToInt32(code, 16), stop = Convert.ToInt32(range.Groups[2].Value, 16);
                var format = code.Length == 2 ? "X2" : "X4";
                if (range.Groups[3].Success)
                {
                    int value = Convert.ToInt32(range.Groups[3].Value, 16);
                    for (int i = 0; i <= Math.Min(1_024, stop - start) && value + i <= char.MaxValue; i++)
                        result[(start + i).ToString(format)] = ((char)(value + i)).ToString();
                }
                else
                {
                    int i = start;
                    foreach (Match value in Regex.Matches(range.Groups[4].Value, @"<([0-9a-fA-F]+)>"))
                    {
                        if (i > stop || i - start > 1_024) break;
                        result[(i++).ToString(format)] = Hex(value.Groups[1].Value, null, unicode: true);
                    }
                }
            }
        return result;
    }

    static string Hex(string text, Dictionary<string, string>? map, bool unicode = false)
    {
        text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (text.Length % 2 == 1) text += "0";
        if (map is { Count: > 0 })
        {
            var output = new StringBuilder();
            for (int i = 0; i < text.Length;)
            {
                if (i + 4 <= text.Length && map.TryGetValue(text.Substring(i, 4), out var word)) { output.Append(word); i += 4; }
                else { output.Append(map.GetValueOrDefault(text.Substring(i, 2), "�")); i += 2; }
            }
            return output.ToString();
        }
        var bytes = Convert.FromHexString(text);
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble)) return Encoding.BigEndianUnicode.GetString(bytes.AsSpan(2));
        return (unicode ? Encoding.BigEndianUnicode : Encoding.Latin1).GetString(bytes);
    }

    static string Literal(string value) => Regex.Replace(value, @"\\(?:([0-7]{1,3})|(\r\n|[\r\n]|.))", match =>
    {
        if (match.Groups[1].Success) return ((char)(Convert.ToInt32(match.Groups[1].Value, 8) & 255)).ToString();
        return match.Groups[2].Value switch { "n" => "\n", "r" => "\r", "t" => "\t", "b" => "\b", "f" => "\f", "\r\n" or "\r" or "\n" => "", var other => other };
    });

    [GeneratedRegex(@"(?s)\b(?<id>\d+)\s+\d+\s+obj\b(?<body>.*?)\bendobj\b")]
    private static partial Regex Object();
    [GeneratedRegex(@"/Type\s*/Page\b")]
    private static partial Regex Page();
    [GeneratedRegex(@"/Contents\s*(?<refs>\[[^\]]*\]|\d+\s+\d+\s+R)")]
    private static partial Regex Contents();
    [GeneratedRegex(@"(\d+)\s+\d+\s+R")]
    private static partial Regex Reference();
    [GeneratedRegex(@"(?s)\A(?<dict>.*?)\bstream\r?\n(?<data>.*?)\r?\nendstream\b")]
    private static partial Regex PdfStream();
    [GeneratedRegex("""(?s)/(?<font>[^\s/<>]+)\s+[-.\d]+\s+Tf\b|(?<text>\((?:\\.|[^\\()])*\)|<[0-9a-fA-F\s]+>|\[(?:\\.|[^\]\\])*\])\s*(?:Tj\b|TJ\b|'|")""")]
    private static partial Regex TextOperation();
    [GeneratedRegex(@"(?s)\((?<literal>(?:\\.|[^\\()])*)\)|<(?<hex>[0-9a-fA-F\s]+)>")]
    private static partial Regex TextToken();
}
