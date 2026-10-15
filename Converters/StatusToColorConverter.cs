using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Converters
{
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            Color green = (Color)ColorConverter.ConvertFromString("#10B981");
            Color red = (Color)ColorConverter.ConvertFromString("#EF4444");
            Color orange = (Color)ColorConverter.ConvertFromString("#F59E0B");
            Color gray = Colors.Gray;

            if (value is ValidationStatus status)
            {
                switch (status)
                {
                    case ValidationStatus.Installed:
                    case ValidationStatus.Success:
                        return green;
                    case ValidationStatus.Missing:
                    case ValidationStatus.Failed:
                        return red;
                    case ValidationStatus.Running:
                    case ValidationStatus.VersionMismatch:
                        return orange;
                }
            }
            else if (value is bool b)
            {
                return b ? green : red;
            }

            return gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
