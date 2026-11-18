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

    // A resource reference follows theme switches; a brush taken from the palette keeps the old colour.
    public static T Paint<T>(this T target, DependencyProperty property, string key) where T : DependencyObject
    {
        if (target is FrameworkElement element) element.SetResourceReference(property, key);
        else if (target is FrameworkContentElement content) content.SetResourceReference(property, key);
        return target;
    }

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

    // Light values for every palette brush; dark ones are the App.xaml originals captured on first use.
    static readonly Dictionary<string, Color> LightColors = new[]
    {
        ("Bg", "#F3F3F5"), ("Surface", "#FFFFFF"), ("Surface2", "#F0F0F2"), ("Surface3", "#E4E4E8"), ("Line", "#E4E4E8"),
        ("LineStrong", "#D0D0D6"), ("ControlBorder", "#8A8A94"), ("LineHover", "#5F5F68"), ("Text", "#1A1A1E"), ("TextSoft", "#36363C"),
        ("TextMuted", "#5C5C64"), ("TextFaint", "#8A8A92"), ("UserBubble", "#1F5FAD"), ("Danger", "#C42B1C"), ("DangerSoft", "#FDE7E9"),
        ("DangerLine", "#F0B4B8"), ("DangerText", "#B3261E"), ("Success", "#107C41"), ("Warning", "#8A5300"), ("WarningSoft", "#FFF4CE"),
        ("WarningLine", "#EBCB7A"), ("Backdrop", "#C8EDEDF0"), ("Shade", "#59000000"), ("SidebarBg", "#EBEBEE"), ("DiffAdd", "#DCF3E2"),
        ("DiffRemove", "#FBE1E3"), ("HoverLight", "#000000"), ("CaptionHover", "#14000000"), ("CaptionPressed", "#0C000000"),
        ("CompactBg", "#F5FFFFFF"), ("CompactLine", "#33000000"), ("CompactDivider", "#24000000"),
    }.ToDictionary(pair => pair.Item1, pair => (Color)ColorConverter.ConvertFromString(pair.Item2));
    static Dictionary<string, Color>? _darkColors;

    public static bool IsLight { get; private set; }

    // Returns whether any palette brush changed, so unrelated system setting changes cost nothing.
    public static bool Apply(ResourceDictionary resources, string? mode)
    {
        IsLight = mode == "light" || mode == "system" && SystemUsesLight();
        _darkColors ??= LightColors.Keys.ToDictionary(key => key, key => ((SolidColorBrush)resources[key]).Color);
        bool changed = false;
        foreach (var (key, light) in LightColors) changed |= Set(resources, key, IsLight ? light : _darkColors[key]);
        Color ColorOf(string key) => ((SolidColorBrush)resources[key]).Color;
        Color surface = ColorOf("Surface"), surface2 = ColorOf("Surface2"), bg = ColorOf("Bg"), fill = ReadWindowsAccent();
        var toward = IsLight ? Colors.Black : Colors.White;
        for (int i = 0; i < 20 && Contrast(fill, surface) < 3.0; i++) fill = Mix(fill, toward, 0.08);
        var dark = Color.FromRgb(0x10, 0x10, 0x12);
        var soft = Mix(surface, fill, IsLight ? 0.12 : 0.22);
        var text = fill;
        for (int i = 0; i < 20 && (Contrast(text, soft) < 4.5 || Contrast(text, surface2) < 4.5); i++) text = Mix(text, toward, 0.08);
        changed |= Set(resources, "Accent", fill);
        changed |= Set(resources, "OnAccent", Contrast(Colors.White, fill) >= 4.5 || Contrast(Colors.White, fill) >= Contrast(dark, fill) ? Colors.White : dark);
        changed |= Set(resources, "AccentSoft", soft);
        changed |= Set(resources, "AccentLine", Mix(surface, fill, IsLight ? 0.45 : 0.55));
        changed |= Set(resources, "AccentText", text);
        return Set(resources, "BannerBg", Mix(bg, fill, IsLight ? 0.1 : 0.14)) | changed;
    }

    // XAML binds palette brushes as DynamicResource, so replacing an entry recolors the whole UI.
    static bool Set(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush brush && brush.Color == color) return false;
        resources[key] = new SolidColorBrush(color).GetAsFrozen();
        return true;
    }

    static bool SystemUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 1;
        }
        catch { return false; }
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
        Play = "", Send = "", Maximize = "", Restore = "", FullScreen = "\uE740", BackToWindow = "\uE73F";
}
