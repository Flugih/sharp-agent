using System.Text;

namespace SharpAgent.Tools;

public static class TextFileIO
{
    public const long MaxTextFileBytes = 20 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding[] BomEncodings = [new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode];

    public static (string Text, Encoding Encoding) Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxTextFileBytes)
            throw new ToolException($"Файл слишком большой для чтения как текст ({PathHelper.FormatSize(info.Length)}).");

        return Decode(File.ReadAllBytes(path));
    }

    internal static (string Text, Encoding Encoding) Decode(byte[] bytes)
    {
        foreach (var encoding in BomEncodings)
            if (bytes.AsSpan().StartsWith(encoding.Preamble))
                return (encoding.GetString(bytes.AsSpan(encoding.Preamble.Length)), encoding);

        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
            throw new ToolException("Это двоичный файл (не текст) — прочитать или изменить его как текст нельзя.");

        try
        {
            return (StrictUtf8.GetString(bytes), new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            var cp1251 = Encoding.GetEncoding(1251);
            return (cp1251.GetString(bytes), cp1251);
        }
    }

    public static string? TryRead(string path, long maxBytes)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= maxBytes ? Read(path).Text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException)
        {
            return null;
        }
    }

    public static void Write(string path, string text, Encoding? encoding = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
    }

    public static (string Text, Encoding? Encoding, string Note) ForScript(string path, string content)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".ps1" or ".psm1" or ".psd1":
                return (content.ReplaceLineEndings("\r\n"), new UTF8Encoding(true), "");

            case ".bat" or ".cmd":
                var text = content.ReplaceLineEndings("\r\n");
                try
                {
                    var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage,
                        EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
                    oem.GetBytes(text);
                    return (text, oem, "");
                }
                catch (Exception ex) when (ex is EncoderFallbackException or ArgumentException or NotSupportedException)
                {
                    return (text, null, " Внимание: в файле есть символы, которых нет в кодировке консоли, поэтому он сохранён " +
                                        "в UTF-8 — добавьте первой строкой «chcp 65001 >nul».");
                }

            default:
                return (content, null, "");
        }
    }

    public static Encoding DetectEncodingOrUtf8(string path)
    {
        try { return Read(path).Encoding; }
        catch { return new UTF8Encoding(false); }
    }
}
