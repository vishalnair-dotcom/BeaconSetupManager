using System.Globalization;
using System.Windows.Data;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Converters;

public sealed class DatabaseEngineSelectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DatabaseEngine engine && parameter is DatabaseEngine option && engine == option;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is DatabaseEngine ? parameter : Binding.DoNothing;
}
