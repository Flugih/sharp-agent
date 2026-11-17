using System.Windows.Media.Imaging;

namespace SharpAgent.Agent;

public static class ImageEncoder
{
    const int MaxSide = 1568;
    const long MaxPassThroughBytes = 1_200_000, MaxSourceBytes = 40L * 1024 * 1024;

    static readonly Dictionary<string, string> PassThroughTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    public static string? ToDataUrl(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxSourceBytes) return null;
            var bytes = File.ReadAllBytes(path);
            var frame = Decode(bytes);
            return PassThroughTypes.TryGetValue(Path.GetExtension(path), out var mime) && bytes.Length <= MaxPassThroughBytes &&
                   (frame == null || Math.Max(frame.PixelWidth, frame.PixelHeight) <= MaxSide && Orientation(frame) == 1)
                ? DataUrl(mime, bytes)
                : frame == null ? null : Reencode(frame);
        }
        catch { return null; }
    }

    static BitmapFrame? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames[0];
        }
        catch { return null; }
    }

    static string Reencode(BitmapFrame frame)
    {
        BitmapSource source = frame;
        double scale = Math.Min(1.0, (double)MaxSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
        int rotation = Orientation(frame) switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        if (scale < 1.0 || rotation != 0)
        {
            var transform = new TransformGroup();
            if (scale < 1.0) transform.Children.Add(new ScaleTransform(scale, scale));
            if (rotation != 0) transform.Children.Add(new RotateTransform(rotation));
            source = new TransformedBitmap(source, transform);
        }
        if (source.Format is var format &&
            (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64) &&
            Encode(new PngBitmapEncoder(), source) is { LongLength: <= 3 * MaxPassThroughBytes } png)
            return DataUrl("image/png", png);
        return DataUrl("image/jpeg", Encode(new JpegBitmapEncoder { QualityLevel = 88 }, new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0)));
    }

    internal static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static string DataUrl(string mime, byte[] bytes) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    static int Orientation(BitmapFrame frame)
    {
        try { return frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort value ? value : 1; }
        catch { return 1; }
    }
}
