using System.Windows.Media.Imaging;

namespace SharpAgent.Tools;

sealed class ScreenStateCache
{
    (IntPtr Window, int Width, int Height, byte[] Pixels, string Path)? _previous;
    (IntPtr Window, int Process, string? Filter, Dictionary<string, int> Elements)? _tree;

    internal string TreeChanges(IntPtr window, int process, string? filter, IEnumerable<string> labels)
    {
        var items = labels.Take(2001).ToArray();
        if (items.Length > 2000) { _tree = null; return ""; }
        var current = items.GroupBy(label => label, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var before = _tree;
        _tree = (window, process, filter, current);
        if (before is not { } old || old.Window != window || old.Process != process ||
            !string.Equals(filter, old.Filter, StringComparison.OrdinalIgnoreCase)) return "";
        var gone = old.Elements.Where(item => item.Value > current.GetValueOrDefault(item.Key)).Take(8)
            .Select(item => AgentRunner.Clip(item.Key, 160)).ToArray();
        return gone.Length == 0 ? "" : "\nБольше нет в дереве окна: " + string.Join("; ", gone) + ".";
    }

    internal (string Path, string Note) Capture(IntPtr window, BitmapSource image, string path, Int32Rect[] masks)
    {
        int width = image.PixelWidth, height = image.PixelHeight;
        var pixels = Pixels(image);
        string note = "";
        if (_previous is { } before && before.Window == window && before.Width == width && before.Height == height)
        {
            if (pixels.AsSpan().SequenceEqual(before.Pixels) && File.Exists(before.Path))
                return (before.Path, "\nИзображение совпадает с предыдущим снимком окна.");
            note = "\n" + Describe(Compare(before.Pixels, pixels, width, height, masks));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ImageEncoder.Encode(new PngBitmapEncoder(), image));
        _previous = (window, width, height, pixels, path);
        return (path, note);
    }

    internal static string Baseline(ToolContext context, BitmapSource image, string id, bool save, string? ignore)
    {
        if (context.AppFolder == null || !Regex.IsMatch(id, @"^[A-Za-z0-9_-]{1,64}$")) throw new ToolException("Нужен допустимый идентификатор эталона.");
        var folder = Path.Combine(context.AppFolder, "scripts", "tests", "screens");
        var path = Path.Combine(folder, id.ToLowerInvariant() + ".png");
        var maskPath = path + ".mask";
        if (!PathHelper.SamePath(folder, PathHelper.FinalPath(folder))) throw new ToolException("Папка эталонов недоступна.");
        if (save)
        {
            _ = Masks(ignore);
            Directory.CreateDirectory(folder);
            if (File.Exists(path) || File.Exists(maskPath)) throw new ToolException("Эталон с таким именем уже существует. Укажите новое имя.");
            var bytes = ImageEncoder.Encode(new PngBitmapEncoder(), image);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(bytes);
            if (!string.IsNullOrWhiteSpace(ignore)) File.WriteAllText(maskPath, ignore);
            return $"Эталон «{id}» сохранён ({image.PixelWidth}×{image.PixelHeight}).";
        }
        if (!File.Exists(path)) throw new ToolException("Эталон не найден.");
        if (!PathHelper.SamePath(path, PathHelper.FinalPath(path)) || new FileInfo(path).Length > 12_000_000)
            throw new ToolException("Файл эталона недоступен.");
        using var input = File.OpenRead(path);
        var baseline = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        if (baseline.PixelWidth != image.PixelWidth || baseline.PixelHeight != image.PixelHeight)
            throw new ToolException("Размер окна отличается от эталона. Сравнение требует одинаковых размеров снимка.");
        if (ignore == null && PathHelper.SamePath(maskPath, PathHelper.FinalPath(maskPath))) ignore = TextFileIO.TryRead(maskPath, 2_000);
        return $"Сравнение с эталоном «{id}». " + Describe(Compare(Pixels(baseline), Pixels(image), image.PixelWidth, image.PixelHeight, Masks(ignore)));
    }

    static byte[] Pixels(BitmapSource source)
    {
        var bitmap = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    internal static (int Count, Int32Rect Area) Compare(byte[] before, byte[] after, int width, int height, Int32Rect[] masks)
    {
        int stride = width * 4, left = width, top = height, right = 0, bottom = 0, count = 0;
        for (int y = 0; y < height; y++)
        {
            var old = before.AsSpan(y * stride, stride);
            var row = after.AsSpan(y * stride, stride);
            if (row.SequenceEqual(old)) continue;
            for (int x = 0; x < width; x++)
            {
                if (row.Slice(x * 4, 4).SequenceEqual(old.Slice(x * 4, 4))) continue;
                bool ignored = false;
                foreach (var mask in masks)
                    if (x >= mask.X && x < mask.X + mask.Width && y >= mask.Y && y < mask.Y + mask.Height) { ignored = true; break; }
                if (ignored) continue;
                count++;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = y + 1;
            }
        }
        return (count, count == 0 ? Int32Rect.Empty : new(left, top, right - left, bottom - top));
    }

    internal static Int32Rect[] Masks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var items = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length > 12) throw new ToolException("Допускается до 12 областей игнорирования.");
        return items.Select(item =>
        {
            var values = item.Split(',', StringSplitOptions.TrimEntries).Select(value =>
                int.TryParse(value, out int n) && n is >= 0 and <= 8192 ? n : -1).ToArray();
            if (values is not [>= 0, >= 0, > 0, > 0]) throw new ToolException("Область игнорирования задаётся как x,y,ширина,высота.");
            return new Int32Rect(values[0], values[1], values[2], values[3]);
        }).ToArray();
    }

    static string Describe((int Count, Int32Rect Area) diff) => diff.Count == 0 ? "Различий вне указанных областей нет." :
        $"Изменилось пикселей: {diff.Count}. Область: x={diff.Area.X}, y={diff.Area.Y}, ширина {diff.Area.Width}, высота {diff.Area.Height}.";
}
