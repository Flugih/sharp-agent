using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace SharpAgent.Tools;

sealed class DocumentInUseException(string path, IOException cause) : IOException(
    Tr($"Документ занят другой программой: {path}. Закройте его и повторите операцию.",
        $"Another program is using {path}. Close the document and retry."), cause)
{
    internal string Path { get; } = path;
    internal static bool Matches(IOException error) => (uint)error.HResult is 0x80070020 or 0x80070021;
}

public static class OfficeText
{
    const int MaxChars = 2_000_000;

    public static string? Extract(string path)
    {
        Func<ZipArchive, string>? extract = Path.GetExtension(path).ToLowerInvariant() switch { ".docx" => Word, ".xlsx" => Excel, ".pptx" => PowerPoint, _ => null };
        if (extract == null) return null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return extract(archive);
        }
        catch (IOException ex) when (DocumentInUseException.Matches(ex)) { throw new DocumentInUseException(path, ex); }
        catch (InvalidDataException) { throw new ToolException("Файл повреждён или защищён паролем — извлечь из него текст не удалось."); }
        catch (XmlException ex) { throw new ToolException("Не удалось разобрать содержимое документа: " + ex.Message); }
    }

    static XmlReader Open(ZipArchiveEntry entry) => XmlReader.Create(entry.Open(),
        new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, CloseInput = true, MaxCharactersInDocument = 1 << 28 });

    static bool Next(XmlReader reader) => reader.Depth <= 1000 ? reader.Read() : throw new InvalidDataException();

    static bool IsText(XmlReader reader) =>
        reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace or XmlNodeType.CDATA;

    static string Word(ZipArchive archive) =>
        Text(archive.GetEntry("word/document.xml") ?? throw new ToolException("В файле нет основного текста (word/document.xml)."),
            new StringBuilder(), true, Links(archive, "word/document.xml")).ToString().TrimEnd();

    static string PowerPoint(ZipArchive archive)
    {
        var slides = OrderedParts(archive, "ppt/presentation.xml", "sldId", "ppt/slides/slide");
        if (slides.Count == 0) throw new ToolException("В презентации нет слайдов.");
        var sb = new StringBuilder();
        for (int i = 0; i < slides.Count && sb.Length < MaxChars; i++)
        {
            var slide = slides[i];
            Text(slide, sb.Append("=== Слайд ").Append(i + 1).Append(" ===\n"), false, Links(archive, slide.FullName)).Append('\n');
            if (Relations(archive, slide.FullName).FirstOrDefault(relation => relation.Type.EndsWith("/notesSlide", StringComparison.Ordinal)) is { } notes &&
                ResolvePart(archive, slide.FullName, notes.Target) is { } entry)
                Text(entry, sb.Append("=== Заметки ").Append(i + 1).Append(" ===\n"), false).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    static StringBuilder Text(ZipArchiveEntry entry, StringBuilder sb, bool word, Dictionary<string, string>? links = null)
    {
        using var reader = Open(entry);
        bool inText = false;
        string? link = null;
        while (Next(reader) && sb.Length < MaxChars)
            switch (reader.NodeType, reader.LocalName)
            {
                case (XmlNodeType.Element, "t"): inText = !reader.IsEmptyElement; break;
                case (XmlNodeType.EndElement, "t"): inText = false; break;
                case (XmlNodeType.Element, "hyperlink") when word:
                    link = RelationshipId(reader) is { } id ? links?.GetValueOrDefault(id) : null;
                    break;
                case (XmlNodeType.EndElement, "hyperlink") when link != null: sb.Append(" [").Append(link).Append(']'); link = null; break;
                case (XmlNodeType.Element, "hlinkClick") when !word:
                    if (RelationshipId(reader) is { } reference && links?.GetValueOrDefault(reference) is { } target)
                        sb.Append(" [").Append(target).Append(']');
                    break;
                case (XmlNodeType.Element, "pStyle") when word:
                    if (reader.GetAttribute("val", reader.NamespaceURI)?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true) sb.Append("# ");
                    break;
                case (XmlNodeType.Element, "tab") or (XmlNodeType.EndElement, "tc") when word: sb.Append('\t'); break;
                case (XmlNodeType.Element, "br") or (XmlNodeType.EndElement, "p"):
                case (XmlNodeType.Element, "cr") when word: sb.Append('\n'); break;
                default: if (inText && IsText(reader)) sb.Append(reader.Value); break;
            }
        return sb;
    }

    static string Excel(ZipArchive archive)
    {
        var shared = SharedStrings(archive);
        var names = SheetNames(archive);
        var sheets = OrderedParts(archive, "xl/workbook.xml", "sheet", "xl/worksheets/sheet");
        if (sheets.Count == 0) throw new ToolException("В книге нет листов с данными.");
        var sb = new StringBuilder();
        for (int i = 0; i < sheets.Count && sb.Length < MaxChars; i++)
        {
            sb.Append("=== Лист: ").Append(i < names.Count ? names[i] : sheets[i].Name).Append(" ===\n");
            using var reader = Open(sheets[i]);
            string? cellType = null;
            bool rowHasCells = false, inValue = false, inInlineText = false;
            var value = new StringBuilder();
            while (Next(reader) && sb.Length < MaxChars)
                switch (reader.NodeType, reader.LocalName)
                {
                    case (XmlNodeType.Element, "row"): rowHasCells = false; break;
                    case (XmlNodeType.Element, "c"):
                        if (rowHasCells) sb.Append('\t');
                        rowHasCells = true;
                        cellType = reader.GetAttribute("t");
                        break;
                    case (XmlNodeType.Element, "v"):
                        inValue = !reader.IsEmptyElement;
                        value.Clear();
                        break;
                    case (XmlNodeType.Element, "t"): inInlineText = !reader.IsEmptyElement; break;
                    case (XmlNodeType.EndElement, "v"):
                        inValue = false;
                        var raw = value.ToString();
                        sb.Append(cellType == "s" && int.TryParse(raw, out var index) && index >= 0 && index < shared.Count ? shared[index] : raw);
                        break;
                    case (XmlNodeType.EndElement, "t"): inInlineText = false; break;
                    case (XmlNodeType.EndElement, "row"): sb.Append('\n'); break;
                    default:
                        if (inValue && IsText(reader)) value.Append(reader.Value);
                        else if (inInlineText && IsText(reader)) sb.Append(reader.Value);
                        break;
                }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    static List<string> SharedStrings(ZipArchive archive)
    {
        var shared = new List<string>();
        if (archive.GetEntry("xl/sharedStrings.xml") is not { } entry) return shared;
        using var reader = Open(entry);
        var current = new StringBuilder();
        bool inText = false, inPhonetic = false;
        while (Next(reader))
            switch (reader.NodeType, reader.LocalName)
            {
                case (XmlNodeType.Element, "si"): current.Clear(); break;
                case (XmlNodeType.Element, "rPh"): inPhonetic = !reader.IsEmptyElement; break;
                case (XmlNodeType.Element, "t"): inText = !reader.IsEmptyElement && !inPhonetic; break;
                case (XmlNodeType.EndElement, "si"): shared.Add(current.ToString()); break;
                case (XmlNodeType.EndElement, "rPh"): inPhonetic = false; break;
                case (XmlNodeType.EndElement, "t"): inText = false; break;
                default: if (inText && IsText(reader)) current.Append(reader.Value); break;
            }
        return shared;
    }

    static List<string> SheetNames(ZipArchive archive)
    {
        var names = new List<string>();
        if (archive.GetEntry("xl/workbook.xml") is not { } entry) return names;
        using var reader = Open(entry);
        while (Next(reader))
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet" && reader.GetAttribute("name") is { } name)
                names.Add(name);
        return names;
    }

    static List<ZipArchiveEntry> NumberedParts(ZipArchive archive, string prefix) =>
        archive.Entries
            .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => int.TryParse(new string(e.Name.Where(char.IsDigit).ToArray()), out var number) ? number : int.MaxValue)
            .ToList();

    sealed record Relation(string Id, string Target, string Type, bool External);

    static IEnumerable<Relation> Relations(ZipArchive archive, string document)
    {
        int slash = document.LastIndexOf('/');
        var path = document[..(slash + 1)] + "_rels/" + document[(slash + 1)..] + ".rels";
        if (archive.GetEntry(path) is not { } entry || entry.Length > 1_000_000) yield break;
        using var reader = Open(entry);
        foreach (var node in XDocument.Load(reader).Descendants().Where(node => node.Name.LocalName == "Relationship"))
            if ((string?)node.Attribute("Id") is { } id && (string?)node.Attribute("Target") is { } target)
                yield return new(id, target, (string?)node.Attribute("Type") ?? "", (string?)node.Attribute("TargetMode") == "External");
    }

    static Dictionary<string, string> Links(ZipArchive archive, string document) => Relations(archive, document)
        .Where(relation => relation.External && relation.Type.EndsWith("/hyperlink", StringComparison.Ordinal))
        .Select(relation => (relation.Id, Url: Uri.TryCreate(relation.Target, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri.GetLeftPart(UriPartial.Path) : null))
        .Where(link => link.Url != null).DistinctBy(link => link.Id).ToDictionary(link => link.Id, link => link.Url!);

    static string? RelationshipId(XmlReader reader) => reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ??
        reader.GetAttribute("id", "http://purl.oclc.org/ooxml/officeDocument/relationships");

    static ZipArchiveEntry? ResolvePart(ZipArchive archive, string document, string target) =>
        Uri.TryCreate(new Uri("https://package/" + document), target, out var uri) && uri.Host == "package" && uri.Scheme == "https"
            ? archive.GetEntry(Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/')) : null;

    static List<ZipArchiveEntry> OrderedParts(ZipArchive archive, string document, string element, string prefix)
    {
        var result = new List<ZipArchiveEntry>();
        var relations = Relations(archive, document).Where(relation => !relation.External).DistinctBy(relation => relation.Id)
            .ToDictionary(relation => relation.Id, relation => relation.Target);
        if (archive.GetEntry(document) is { } entry && relations.Count > 0)
        {
            using var reader = Open(entry);
            while (Next(reader))
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == element && RelationshipId(reader) is { } id &&
                    relations.TryGetValue(id, out var target) && ResolvePart(archive, document, target) is { } part) result.Add(part);
        }
        return result.Count == 0 ? NumberedParts(archive, prefix) : result;
    }
}
