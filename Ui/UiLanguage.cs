using System.Windows.Markup;
using System.Windows.Data;

namespace SharpAgent.Ui;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocalizedExtension(string russian, string english) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(UiLanguage.IsRussian)) { Source = UiLanguage.Current.Value, Mode = BindingMode.OneWay, Converter = new Translation(russian, english) }.ProvideValue(serviceProvider);

    sealed class Translation(string russian, string english) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? russian : english;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

internal sealed class UiLanguage : INotifyPropertyChanged
{
    internal static readonly Lazy<UiLanguage> Current = new(() =>
    {
        var language = new UiLanguage();
        try { language.IsRussian = File.Exists(AppPaths.EnvPath) && AppConfig.IsRussianLanguage(DotEnv.Parse(AppPaths.EnvPath).GetValueOrDefault("AGENT_LANGUAGE")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return language;
    });

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsRussian { get; private set; }
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Tr("ru-RU", "en-US"));
    public static string Tr(string russian, string english) => Current.Value.IsRussian ? russian : english;

    public void Apply(bool russian)
    {
        if (IsRussian == russian) return;
        IsRussian = russian;
        PropertyChanged?.Invoke(this, new(nameof(IsRussian)));
    }
}
