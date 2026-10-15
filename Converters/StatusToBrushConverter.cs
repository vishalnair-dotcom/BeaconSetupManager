using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Converters
{
    public class StatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ValidationStatus status)
            {
                switch (status)
                {
                    case ValidationStatus.Installed:
                    case ValidationStatus.Success:
                        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")); // Emerald Green
                    case ValidationStatus.Missing:
                    case ValidationStatus.Failed:
                        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")); // Red
                    case ValidationStatus.Running:
                    case ValidationStatus.VersionMismatch:
                        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")); // Amber Orange
                }
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
