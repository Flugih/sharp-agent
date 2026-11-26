using Microsoft.Win32;

namespace SharpAgent.Ui;

public static class Theme
{
    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static Brush Line => Get("Line");
    public static Brush Text => Get("Text");
    public static Brush TextSoft => Get("TextSoft");
    public static Brush DangerText => Get("DangerText");

    public static FontFamily Icons => (FontFamily)Application.Current.Resources["IconFont"];
    public static FontFamily Mono => (FontFamily)Application.Current.Resources["MonoFont"];
    public static readonly FontFamily Ui = new(string.Join(", ", new[]
    {
        SystemFonts.MessageFontFamily.Source, "Segoe UI", "Helvetica", "Arial", "Global User Interface"
    }.Distinct(StringComparer.OrdinalIgnoreCase)));

    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    // A resource reference follows theme switches; a brush taken from the palette keeps the old colour.
    public static T Paint<T>(this T target, DependencyProperty property, string key) where T : DependencyObject
    {
        if (target is FrameworkElement element) element.SetResourceReference(property, key);
        else if (target is FrameworkContentElement content) content.SetResourceReference(property, key);
        return target;
    }

    public static Button IconButton(string glyph, string hint, double size = 32, double fontSize = 16)
    {
        var button = new Button { Style = Style("IconButton"), Content = glyph, Width = size, Height = size, FontSize = fontSize, ToolTip = hint };
        AutomationProperties.SetName(button, hint);
        return button;
    }

    public static Border KeyBadge(string keys) => new Border
    {
        CornerRadius = new(6), Padding = new(6, 2, 6, 2),
        Child = new TextBlock { Text = keys, FontSize = 12 }.Paint(TextBlock.ForegroundProperty, "TextSoft"),
    }.Paint(Border.BackgroundProperty, "TrackStrong");

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
        ("Bg", "#FFFFFF"), ("ChromeBg", "#F3F3F3"), ("Surface", "#F4F4F4"), ("CompactTranscriptBg", "#FFFFFF"),
        ("ChatModeTrack", "#F3F3F3"), ("ChatModeSelected", "#FFFFFF"), ("ChatModeShadow", "#E0E0E0"), ("ChatModeText", "#5E5E5E"),
        ("SearchGlass", "#EBFAFAFA"), ("SearchHighlight", "#D7C896"),
        ("Surface2", "#FFFFFF"), ("Surface3", "#E6E6E6"), ("BlockBg", "#F4F4F4"), ("Raised", "#FFFFFF"), ("RaisedLine", "#D9D9D9"),
        ("Fill", "#EFEFEF"), ("ControlBg", "#E9E9E9"), ("RowHover", "#0B000000"), ("ComposerBg", "#FFFFFF"), ("ComposerLine", "#E0E0E0"),
        ("ComposerStrip", "#F2F2F2"), ("Line", "#E5E5E5"), ("LineStrong", "#D9D9D9"), ("Track", "#D4D4D4"), ("TrackStrong", "#B4B4B4"),
        ("Text", "#0D0D0D"), ("TextStrong", "#000000"), ("TextSoft", "#3C3C3C"), ("TextDim", "#5E5E5E"), ("TextMuted", "#686868"),
        ("TextFaint", "#A0A0A0"), ("UserBubbleText", "#0D0D0D"), ("PrimaryBg", "#181818"), ("PrimaryFg", "#FFFFFF"), ("Danger", "#C42B1C"),
        ("DangerText", "#BC2A1B"), ("DangerSoft", "#FDE7E9"), ("Warning", "#A84300"), ("Success", "#0E7439"), ("DiffAdd", "#E6F4EA"),
        ("DiffAddText", "#17722F"), ("DiffRemove", "#FBE1E3"), ("Shade", "#59000000"), ("HoverLight", "#000000"),
        ("CodeKeyword", "#70338B"), ("CodeString", "#3C6422"), ("CodeNumber", "#175E8B"), ("CodeName", "#8B4311"),
        ("CaptionHover", "#14000000"), ("CaptionPressed", "#0C000000"),
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
        Color bg = ColorOf("Bg"), fill = ReadWindowsAccent();
        Color toward = IsLight ? Colors.Black : Colors.White, away = IsLight ? Colors.White : Colors.Black;
        for (int i = 0; i < 20 && Contrast(fill, bg) < 3.0; i++) fill = Mix(fill, toward, 0.08);
        var dark = Color.FromRgb(0x10, 0x10, 0x12);
        // Accent text must stay readable on the hardest surface it is used on: Raised in dark, Surface3 in light.
        Color text = fill, hardest = ColorOf(IsLight ? "Surface3" : "Raised");
        for (int i = 0; i < 20 && Contrast(text, hardest) < 4.5; i++) text = Mix(text, toward, 0.08);
        Color bubble = Mix(away, fill, IsLight ? 0.15 : 0.6), bubbleText = ColorOf("UserBubbleText");
        for (int i = 0; i < 20 && Contrast(bubbleText, bubble) < 4.5; i++) bubble = Mix(bubble, away, 0.08);
        changed |= Set(resources, "Accent", fill);
        changed |= Set(resources, "OnAccent", Contrast(Colors.White, fill) >= 4.5 || Contrast(Colors.White, fill) >= Contrast(dark, fill) ? Colors.White : dark);
        changed |= Set(resources, "AccentText", text);
        return Set(resources, "UserBubble", bubble) | changed;
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
        Play = "\uF5B0", Send = "", Maximize = "", Restore = "", FullScreen = "\uE740", BackToWindow = "\uE73F";
    public const string Laptop = "\uE7F8", Wifi = "\uE701";
}
