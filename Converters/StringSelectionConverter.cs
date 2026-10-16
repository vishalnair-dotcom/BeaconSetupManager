using System.Globalization;
using System.Windows.Data;

namespace BeaconSetupManager.Converters;

public sealed class StringSelectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string selected && parameter is string option && selected == option;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is string ? parameter : Binding.DoNothing;
}
