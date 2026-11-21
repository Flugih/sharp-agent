namespace SharpAgent.Core;

public static class Tokens
{
    public static string Format(long value)
    {
        double abs = Math.Abs((double)value);
        var culture = UiLanguage.Culture;
        if (abs >= 1e9) return (value / 1e9).ToString("0.##", culture) + Tr(" млрд", "B");
        if (abs >= 1e6) return (value / 1e6).ToString(abs >= 1e8 ? "0.#" : "0.##", culture) + Tr(" млн", "M");
        if (abs >= 1e4) return (value / 1e3).ToString(abs >= 1e5 ? "0" : "0.#", culture) + Tr(" тыс.", "K");
        return value.ToString("N0", culture);
    }

    public static string ToMillions(long value) => (value / 1e6).ToString("0.###", UiLanguage.Culture);

    public static string Percent(double fraction) => Math.Round(Math.Clamp(fraction, 0, 1) * 100).ToString("0", UiLanguage.Culture) + "%";

    public static string Duration(TimeSpan time)
    {
        int seconds = Math.Max(1, (int)Math.Round(time.TotalSeconds));
        var second = Tr("с", "s");
        var minute = Tr("мин", "min");
        if (seconds < 60) return $"{seconds} {second}";
        if (seconds < 3600) return $"{seconds / 60} {minute}" + (seconds % 60 > 0 ? $" {seconds % 60} {second}" : "");
        return $"{seconds / 3600} {Tr("ч", "h")}" + (seconds % 3600 / 60 > 0 ? $" {seconds % 3600 / 60} {minute}" : "");
    }
}
