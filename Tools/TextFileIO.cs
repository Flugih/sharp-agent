namespace SharpAgent.Tools;

public static class TextFileIO
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static readonly Encoding[] BomEncodings = [new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode];

    public static (string Text, Encoding Encoding) Read(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > 20 * 1024 * 1024)
            throw new ToolException($"Файл слишком большой для чтения как текст ({PathHelper.FormatSize(length)}).");
        return Decode(File.ReadAllBytes(path));
    }

    internal static Encoding? DetectBom(byte[] bytes) => BomEncodings.FirstOrDefault(encoding => bytes.AsSpan().StartsWith(encoding.Preamble));

    internal static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        if (DetectBom(bytes) is { } encoding) return (encoding.GetString(bytes.AsSpan(encoding.Preamble.Length)), encoding);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
            throw new ToolException("Это двоичный файл (не текст) — прочитать или изменить его как текст нельзя.");
        try { return (StrictUtf8.GetString(bytes), new UTF8Encoding(false)); }
        catch (DecoderFallbackException)
        {
            var cp1251 = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            return (cp1251.GetString(bytes), cp1251);
        }
    }

    public static string? TryRead(string path, long maxBytes)
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= maxBytes ? Read(path).Text : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException) { return null; }
    }

    public static void Write(string path, string text, Encoding? encoding)
    {
        encoding ??= new UTF8Encoding(false);
        var bytes = encoding.GetBytes(text);
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        file.Write(encoding.GetPreamble());
        file.Write(bytes);
    }

    public static void Append(string path, string text)
    {
        Encoding encoding;
        try { encoding = Read(path).Encoding; }
        catch { encoding = new UTF8Encoding(false); }
        var bytes = encoding.GetBytes(text);
        using var file = new FileStream(path, FileMode.Append);
        file.Write(bytes);
    }

    public static (string Text, Encoding? Encoding, string Note) ForScript(string path, string content)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".ps1" or ".psm1" or ".psd1") return (content.ReplaceLineEndings("\r\n"), new UTF8Encoding(true), "");
        if (extension is not (".bat" or ".cmd")) return (content, null, "");
        var text = content.ReplaceLineEndings("\r\n");
        try
        {
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            oem.GetBytes(text);
            return (text, oem, "");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return (text, null, " Внимание: в файле есть символы, которых нет в кодировке консоли, поэтому он сохранён " +
                                "в UTF-8 — добавьте первой строкой «chcp 65001 >nul».");
        }
    }
}
