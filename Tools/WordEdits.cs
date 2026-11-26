using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace SharpAgent.Tools;

static class WordEdits
{
    internal const string Name = "edit_word_document";
    const string Part = "word/document.xml";
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    internal static AgentTool Tool() => new()
    {
        Name = Name,
        Description = "Edit an existing .docx in place. Read its text first. Each edit needs a unique exact match within one plain-text paragraph, even across formatting runs. Supply replacement to change text or insert_after to add paragraphs after the match. Keeps the rest of the document.",
        Parameters = Parameters(),
        Handler = AgentTool.Sync(Edit),
    };

    static JsonObject Parameters()
    {
        var schema = Schema.Object(new Prop("path", "string", "Existing .docx path.", true), new Prop("edits", "array", "Ordered edits, applied together.", true));
        schema["properties"]!["edits"]!["items"] = Schema.Object(new Prop("match", "string", "Unique exact passage in one paragraph.", true),
            new Prop("replacement", "string", "Replacement text. An empty string removes the match."),
            new Prop("insert_after", "string", "Text to insert after the matched paragraph. Separate paragraphs with newlines."));
        return schema;
    }

    static string Edit(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var path = PathHelper.FinalPath(context.Resolve(args.Required("path")));
        context.Resolve(path);
        context.GuardSecrets(path);
        context.GuardWrite(path);
        if (!Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ToolException("Укажите существующий файл .docx.");
        var edits = args.Array("edits");
        if (edits.Count is < 1 or > 50) throw new ToolException("Укажите от 1 до 50 правок.");
        var stage = Path.Combine(Path.GetDirectoryName(path)!, $".sharp-agent-{Guid.NewGuid():N}.tmp");
        var staged = false;
        try
        {
            long revision = PathHelper.Revision(path);
            if (revision == 0) throw new ToolException("Не удалось проверить версию документа.");
            using (var input = File.OpenRead(path))
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write))
            {
                staged = true;
                input.CopyTo(output);
            }
            using (var zip = ZipFile.Open(stage, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry(Part) ?? throw new ToolException("В документе нет основного текста.");
                XDocument doc;
                using (var reader = XmlReader.Create(entry.Open(), new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, CloseInput = true, MaxCharactersInDocument = 16_000_000 }))
                    doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                var body = doc.Root?.Element(W + "body") ?? throw new ToolException("Формат текста документа не поддерживается.");
                foreach (var node in edits)
                {
                    ct.ThrowIfCancellationRequested();
                    if (node is not JsonObject edit || Str(edit["match"]) is not { Length: > 0 } match ||
                        edit.ContainsKey("replacement") == edit.ContainsKey("insert_after"))
                        throw new ToolException("Для каждой правки укажите match и ровно одно поле: replacement или insert_after.");
                    var replacement = Str(edit["replacement"]);
                    var insertion = Str(edit["insert_after"]);
                    if (replacement == null && string.IsNullOrWhiteSpace(insertion))
                        throw new ToolException("Укажите текст правки.");
                    Apply(body, match, replacement, insertion);
                }
                entry.Delete();
                using var destination = zip.CreateEntry(Part).Open();
                doc.Save(destination, SaveOptions.DisableFormatting);
            }
            ct.ThrowIfCancellationRequested();
            if (!PathHelper.SamePath(path, PathHelper.FinalPath(path)) || PathHelper.Revision(path) != revision)
                throw new ToolException("Документ изменился во время редактирования. Прочитайте его заново.");
            File.Replace(stage, path, null);
            return $"Документ изменён: {path} ({edits.Count} правок).";
        }
        catch (IOException ex) when (DocumentInUseException.Matches(ex)) { throw new DocumentInUseException(path, ex); }
        catch (XmlException) { throw new ToolException("В документе или тексте правки есть недопустимые XML-данные."); }
        finally { if (staged) File.Delete(stage); }
    }

    static void Apply(XElement body, string match, string? replacement, string? insertion)
    {
        var hits = body.Descendants(W + "p").Where(paragraph => !paragraph.Ancestors(W + "p").Any())
            .Select(paragraph =>
            {
                var text = string.Concat(paragraph.Descendants(W + "t").Select(node => node.Value));
                return (Paragraph: paragraph, Text: text, Offset: text.IndexOf(match, StringComparison.Ordinal));
            }).Where(hit => hit.Offset >= 0).Take(2).ToArray();
        if (hits.Length != 1 || hits[0].Text.IndexOf(match, hits[0].Offset + 1, StringComparison.Ordinal) >= 0)
            throw new ToolException("Фрагмент должен встретиться ровно один раз в одном абзаце. Прочитайте документ и уточните match.");
        var (paragraph, _, offset) = hits[0];
        if (insertion != null)
        {
            paragraph.AddAfterSelf(insertion.ReplaceLineEndings("\n").Split('\n').Select(line =>
                new XElement(W + "p", new XElement(W + "r", TextNode(line)))));
            return;
        }
        if (replacement!.Contains('\n') || replacement.Contains('\r') || replacement.Contains('\t') ||
            paragraph.Elements().Any(node => node.Name != W + "pPr" && (node.Name != W + "r" ||
                node.Elements().Any(part => part.Name != W + "rPr" && part.Name != W + "t"))))
            throw new ToolException("Для этой замены нужен обычный текстовый абзац без полей, ссылок и переносов. Для новых абзацев используйте insert_after.");
        XmlConvert.VerifyXmlChars(replacement);
        int end = offset + match.Length, position = 0;
        foreach (var node in paragraph.Elements(W + "r").Elements(W + "t"))
        {
            var current = node.Value;
            int next = position + current.Length;
            if (next > offset && position < end)
            {
                node.Value = current[..Math.Max(0, offset - position)] + (position <= offset ? replacement : "") +
                    current[Math.Clamp(end - position, 0, current.Length)..];
                node.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            }
            position = next;
        }
    }

    static XElement TextNode(string value) => new(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), XmlConvert.VerifyXmlChars(value));
}
