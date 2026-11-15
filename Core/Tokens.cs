using System.Globalization;

namespace SharpAgent.Core;

public static class Tokens
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Format(long value)
    {
        double abs = Math.Abs((double)value);
        if (abs >= 1e9) return (value / 1e9).ToString("0.##", Ru) + " млрд";
        if (abs >= 1e6) return (value / 1e6).ToString(abs >= 1e8 ? "0.#" : "0.##", Ru) + " млн";
        if (abs >= 1e4) return (value / 1e3).ToString(abs >= 1e5 ? "0" : "0.#", Ru) + " тыс.";
        return value.ToString("N0", Ru);
    }

    public static string ToMillions(long value) => (value / 1e6).ToString("0.###", Ru);

    public static string Percent(double fraction) =>
        Math.Round(Math.Clamp(fraction, 0, 1) * 100).ToString("0", Ru) + "%";

    public static string Duration(TimeSpan time)
    {
        int seconds = Math.Max(1, (int)Math.Round(time.TotalSeconds));
        if (seconds < 60) return $"{seconds} с";
        if (seconds < 3600) return $"{seconds / 60} мин" + (seconds % 60 > 0 ? $" {seconds % 60} с" : "");
        return $"{seconds / 3600} ч" + (seconds % 3600 / 60 > 0 ? $" {seconds % 3600 / 60} мин" : "");
    }
}
