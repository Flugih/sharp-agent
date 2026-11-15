using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SharpAgent.Ui;

public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new CornerRadius(value is double height && height > 0 ? height / 2 : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
