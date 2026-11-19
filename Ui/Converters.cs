using System.Windows.Data;

namespace SharpAgent.Ui;

// A pill's corner radius is half its height: WPF turns a larger one into an oval on a wide box.
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new CornerRadius(value is double height and > 0 ? height / 2 : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Shows a trailing "(Ctrl+N)" of a tooltip as a key badge, so tooltip strings stay as they are.
public sealed partial class ShortcutTipConverter : IValueConverter
{
    [GeneratedRegex(@"^(.+?)\s*\(((?:Ctrl|Alt|Shift|Win|Esc|Enter|Del|Tab|F\d)[^()]*)\)$")]
    private static partial Regex Shortcut();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || Shortcut().Match(text) is not { Success: true } match) return value;
        var keys = Theme.KeyBadge(match.Groups[2].Value);
        keys.Margin = new(8, 0, 0, 0);
        keys.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = match.Groups[1].Value, VerticalAlignment = VerticalAlignment.Center }, keys },
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
