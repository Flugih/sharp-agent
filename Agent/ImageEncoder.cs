using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SharpAgent.Agent;

public static class ImageEncoder
{
    private const int MaxSide = 1568;
    private const long MaxPassThroughBytes = 1_200_000;
    private const long MaxSourceBytes = 40L * 1024 * 1024;

    private static readonly Dictionary<string, string> PassThroughTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public static string? ToDataUrl(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxSourceBytes) return null;
            var bytes = File.ReadAllBytes(path);
            var frame = Decode(bytes);

            bool fitsAsIs = frame == null ||
                            (Math.Max(frame.PixelWidth, frame.PixelHeight) <= MaxSide && Orientation(frame) == 1);
            if (PassThroughTypes.TryGetValue(Path.GetExtension(path), out var mime) &&
                bytes.Length <= MaxPassThroughBytes && fitsAsIs)
                return DataUrl(mime, bytes);

            return frame == null ? null : Reencode(frame);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapFrame? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch
        {
            return null;
        }
    }

    private static string Reencode(BitmapFrame frame)
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

        bool hasAlpha = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32 ||
                        source.Format == PixelFormats.Rgba64 || source.Format == PixelFormats.Prgba64;
        if (hasAlpha)
        {
            var png = Encode(new PngBitmapEncoder(), source);
            if (png.Length <= 3 * MaxPassThroughBytes) return DataUrl("image/png", png);
        }

        var opaque = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        return DataUrl("image/jpeg", Encode(new JpegBitmapEncoder { QualityLevel = 88 }, opaque));
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string DataUrl(string mime, byte[] bytes) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort value
                ? value
                : 1;
        }
        catch
        {
            return 1;
        }
    }
}
