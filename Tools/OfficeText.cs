using System.IO.Compression;
using System.Xml;

namespace SharpAgent.Tools;

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
            new StringBuilder(), true).ToString().TrimEnd();

    static string PowerPoint(ZipArchive archive)
    {
        var slides = NumberedParts(archive, "ppt/slides/slide");
        if (slides.Count == 0) throw new ToolException("В презентации нет слайдов.");
        var sb = new StringBuilder();
        for (int i = 0; i < slides.Count && sb.Length < MaxChars; i++)
            Text(slides[i], sb.Append("=== Слайд ").Append(i + 1).Append(" ===\n"), false).Append('\n');
        return sb.ToString().TrimEnd();
    }

    static StringBuilder Text(ZipArchiveEntry entry, StringBuilder sb, bool word)
    {
        using var reader = Open(entry);
        bool inText = false;
        while (Next(reader) && sb.Length < MaxChars)
            switch (reader.NodeType, reader.LocalName)
            {
                case (XmlNodeType.Element, "t"): inText = !reader.IsEmptyElement; break;
                case (XmlNodeType.EndElement, "t"): inText = false; break;
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
        var sheets = NumberedParts(archive, "xl/worksheets/sheet");
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
}
