using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Converters
{
    public class StatusToDownloadButtonVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ValidationStatus status)
            {
                if (status == ValidationStatus.Missing || status == ValidationStatus.Failed || status == ValidationStatus.VersionMismatch)
                {
                    return Visibility.Visible;
                }
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
