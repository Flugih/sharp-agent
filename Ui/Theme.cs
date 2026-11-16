using Microsoft.Win32;

namespace SharpAgent.Ui;

public static class Theme
{
    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static Brush Bg => Get("Bg");
    public static Brush Surface => Get("Surface");
    public static Brush Surface2 => Get("Surface2");
    public static Brush Surface3 => Get("Surface3");
    public static Brush Line => Get("Line");
    public static Brush LineStrong => Get("LineStrong");
    public static Brush Text => Get("Text");
    public static Brush TextSoft => Get("TextSoft");
    public static Brush TextMuted => Get("TextMuted");
    public static Brush Accent => Get("Accent");
    public static Brush AccentLine => Get("AccentLine");
    public static Brush AccentText => Get("AccentText");
    public static Brush DangerSoft => Get("DangerSoft");
    public static Brush DangerLine => Get("DangerLine");
    public static Brush DangerText => Get("DangerText");
    public static Brush Success => Get("Success");
    public static Brush Warning => Get("Warning");
    public static Brush WarningSoft => Get("WarningSoft");
    public static Brush WarningLine => Get("WarningLine");

    public static FontFamily Icons => (FontFamily)Application.Current.Resources["IconFont"];
    public static FontFamily Mono => (FontFamily)Application.Current.Resources["MonoFont"];
    public static readonly FontFamily Ui = new("Segoe UI");

    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    public static Button IconButton(string glyph, string hint, double size = 28, double fontSize = 12)
    {
        var button = new Button { Style = Style("IconButton"), Content = glyph, Width = size, Height = size, FontSize = fontSize, ToolTip = hint };
        AutomationProperties.SetName(button, hint);
        return button;
    }

    public static void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    public static void ApplySystemAccent(ResourceDictionary resources)
    {
        Color ColorOf(string key) => ((SolidColorBrush)resources[key]).Color;
        void Set(string key, Color color) => resources[key] = new SolidColorBrush(color).GetAsFrozen();
        Color surface = ColorOf("Surface"), surface2 = ColorOf("Surface2"), bg = ColorOf("Bg"), fill = ReadWindowsAccent();
        for (int i = 0; i < 20 && Contrast(fill, surface) < 3.0; i++) fill = Mix(fill, Colors.White, 0.08);
        var dark = Color.FromRgb(0x10, 0x10, 0x12);
        var soft = Mix(surface, fill, 0.22);
        var text = fill;
        for (int i = 0; i < 20 && (Contrast(text, soft) < 4.5 || Contrast(text, surface2) < 4.5); i++) text = Mix(text, Colors.White, 0.08);
        Set("Accent", fill);
        Set("OnAccent", Contrast(Colors.White, fill) >= 4.5 || Contrast(Colors.White, fill) >= Contrast(dark, fill) ? Colors.White : dark);
        Set("AccentSoft", soft);
        Set("AccentLine", Mix(surface, fill, 0.55));
        Set("AccentText", text);
        Set("BannerBg", Mix(bg, fill, 0.14));
    }

    static Color ReadWindowsAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr) return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        }
        catch { }
        var glass = SystemParameters.WindowGlassColor;
        return glass is { A: 0, R: 0, G: 0, B: 0 } ? Color.FromRgb(0x00, 0x78, 0xD4) : Color.FromRgb(glass.R, glass.G, glass.B);
    }

    static Color Mix(Color a, Color b, double amount) => Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount), (byte)Math.Round(a.B + (b.B - a.B) * amount));

    static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance(Color c) => 0.2126 * Channel(c.R / 255.0) + 0.7152 * Channel(c.G / 255.0) + 0.0722 * Channel(c.B / 255.0);

    static double Channel(double s) => s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
}

public static class Glyphs
{
    public const string Plugin = "", Code = "", Undo = "", Rename = "", More = "", Shield = "", Document = "",
        Folder = "", NewFolder = "", Picture = "", Edit = "", Save = "", Delete = "", Copy = "", Move = "", Search = "",
        Terminal = "", Open = "", OpenWith = "", Reveal = "", Info = "", View = "", Check = "", Cancel = "",
        Warning = "", ChevronRight = "", ChevronDown = "", Tool = "", Lock = "", Globe = "", Refresh = "", Lightning = "",
        Play = "", Send = "", Maximize = "", Restore = "";
}
