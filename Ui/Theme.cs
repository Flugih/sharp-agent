using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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
        var button = new Button
        {
            Style = Style("IconButton"), Content = glyph, Width = size, Height = size, FontSize = fontSize, ToolTip = hint,
        };
        AutomationProperties.SetName(button, hint);
        return button;
    }

    public static void ApplySystemAccent(ResourceDictionary resources)
    {
        var surface = ColorOf(resources, "Surface");
        var surface2 = ColorOf(resources, "Surface2");
        var bg = ColorOf(resources, "Bg");

        var fill = ReadWindowsAccent();
        for (int i = 0; i < 20 && Contrast(fill, surface) < 3.0; i++) fill = Mix(fill, Colors.White, 0.08);

        var dark = Color.FromRgb(0x10, 0x10, 0x12);
        var onAccent = Contrast(Colors.White, fill) >= 4.5 || Contrast(Colors.White, fill) >= Contrast(dark, fill)
            ? Colors.White
            : dark;

        var soft = Mix(surface, fill, 0.22);
        var line = Mix(surface, fill, 0.55);

        var text = fill;
        for (int i = 0; i < 20 && (Contrast(text, soft) < 4.5 || Contrast(text, surface2) < 4.5); i++)
            text = Mix(text, Colors.White, 0.08);

        Set(resources, "Accent", fill);
        Set(resources, "OnAccent", onAccent);
        Set(resources, "AccentSoft", soft);
        Set(resources, "AccentLine", line);
        Set(resources, "AccentText", text);
        Set(resources, "BannerBg", Mix(bg, fill, 0.14));
    }

    private static Color ReadWindowsAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }
        catch
        {
        }

        var glass = SystemParameters.WindowGlassColor;
        return glass.A == 0 && glass.R == 0 && glass.G == 0 && glass.B == 0
            ? Color.FromRgb(0x00, 0x78, 0xD4)
            : Color.FromRgb(glass.R, glass.G, glass.B);
    }

    private static Color ColorOf(ResourceDictionary resources, string key) => ((SolidColorBrush)resources[key]).Color;

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    public static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte value)
        {
            double s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}

public static class Glyphs
{
    public const string Plugin = "";
    public const string Code = "";
    public const string Undo = "";
    public const string Rename = "";
    public const string More = "";
    public const string Shield = "";
    public const string Document = "";
    public const string Folder = "";
    public const string NewFolder = "";
    public const string Picture = "";
    public const string Edit = "";
    public const string Save = "";
    public const string Delete = "";
    public const string Copy = "";
    public const string Move = "";
    public const string Search = "";
    public const string Terminal = "";
    public const string Open = "";
    public const string OpenWith = "";
    public const string Reveal = "";
    public const string Info = "";
    public const string View = "";
    public const string Check = "";
    public const string Cancel = "";
    public const string Warning = "";
    public const string ChevronRight = "";
    public const string ChevronDown = "";
    public const string Tool = "";
    public const string Lock = "";
    public const string Globe = "";
    public const string Refresh = "";
    public const string Lightning = "\uE945";
    public const string Play = "\uE768";
    public const string Send = "\uE74A";
    public const string Maximize = "";
    public const string Restore = "";
}
