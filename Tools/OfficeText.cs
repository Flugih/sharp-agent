using System.IO.Compression;
using System.Text;
using System.Xml;

namespace SharpAgent.Tools;

public static class OfficeText
{
    private const int MaxChars = 2_000_000;

    public static bool CanRead(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx";

    public static string Extract(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".docx" => Word(archive),
                ".xlsx" => Excel(archive),
                ".pptx" => PowerPoint(archive),
                _ => throw new ToolException("Этот формат не поддерживается."),
            };
        }
        catch (InvalidDataException)
        {
            throw new ToolException("Файл повреждён или защищён паролем — извлечь из него текст не удалось.");
        }
        catch (XmlException ex)
        {
            throw new ToolException("Не удалось разобрать содержимое документа: " + ex.Message);
        }
    }

    private static XmlReader Open(ZipArchiveEntry entry) =>
        XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, CloseInput = true });

    private static bool IsText(XmlReader reader) =>
        reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace or XmlNodeType.CDATA;

    private static string Word(ZipArchive archive)
    {
        var entry = archive.GetEntry("word/document.xml")
                    ?? throw new ToolException("В файле нет основного текста (word/document.xml).");
        var sb = new StringBuilder();
        using var reader = Open(entry);
        bool inText = false;
        while (reader.Read() && sb.Length < MaxChars)
        {
            switch (reader.NodeType, reader.LocalName)
            {
                case (XmlNodeType.Element, "t"): inText = !reader.IsEmptyElement; break;
                case (XmlNodeType.EndElement, "t"): inText = false; break;
                case (XmlNodeType.Element, "tab") or (XmlNodeType.EndElement, "tc"): sb.Append('\t'); break;
                case (XmlNodeType.Element, "br" or "cr") or (XmlNodeType.EndElement, "p"): sb.Append('\n'); break;
                default: if (inText && IsText(reader)) sb.Append(reader.Value); break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Excel(ZipArchive archive)
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
            while (reader.Read() && sb.Length < MaxChars)
            {
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
                        sb.Append(cellType == "s" && int.TryParse(raw, out var index) && index >= 0 && index < shared.Count
                            ? shared[index]
                            : raw);
                        break;
                    case (XmlNodeType.EndElement, "t"): inInlineText = false; break;
                    case (XmlNodeType.EndElement, "row"): sb.Append('\n'); break;
                    default:
                        if (inValue && IsText(reader)) value.Append(reader.Value);
                        else if (inInlineText && IsText(reader)) sb.Append(reader.Value);
                        break;
                }
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static List<string> SharedStrings(ZipArchive archive)
    {
        var shared = new List<string>();
        if (archive.GetEntry("xl/sharedStrings.xml") is not { } entry) return shared;

        using var reader = Open(entry);
        var current = new StringBuilder();
        bool inText = false, inPhonetic = false;
        while (reader.Read())
        {
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
        }
        return shared;
    }

    private static List<string> SheetNames(ZipArchive archive)
    {
        var names = new List<string>();
        if (archive.GetEntry("xl/workbook.xml") is not { } entry) return names;
        using var reader = Open(entry);
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet" && reader.GetAttribute("name") is { } name)
                names.Add(name);
        return names;
    }

    private static string PowerPoint(ZipArchive archive)
    {
        var slides = NumberedParts(archive, "ppt/slides/slide");
        if (slides.Count == 0) throw new ToolException("В презентации нет слайдов.");

        var sb = new StringBuilder();
        for (int i = 0; i < slides.Count && sb.Length < MaxChars; i++)
        {
            sb.Append("=== Слайд ").Append(i + 1).Append(" ===\n");
            using var reader = Open(slides[i]);
            bool inText = false;
            while (reader.Read() && sb.Length < MaxChars)
            {
                switch (reader.NodeType, reader.LocalName)
                {
                    case (XmlNodeType.Element, "t"): inText = !reader.IsEmptyElement; break;
                    case (XmlNodeType.EndElement, "t"): inText = false; break;
                    case (XmlNodeType.Element, "br") or (XmlNodeType.EndElement, "p"): sb.Append('\n'); break;
                    default: if (inText && IsText(reader)) sb.Append(reader.Value); break;
                }
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static List<ZipArchiveEntry> NumberedParts(ZipArchive archive, string prefix) =>
        archive.Entries
            .Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => NumberIn(e.Name))
            .ToList();

    private static int NumberIn(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : int.MaxValue;
    }
}
