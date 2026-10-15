using System;
using System.Globalization;
using System.Windows.Data;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Converters
{
    public class StatusToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ValidationStatus status)
            {
                switch (status)
                {
                    case ValidationStatus.Installed:
                    case ValidationStatus.Success:
                        return "✓ Installed";
                    case ValidationStatus.Missing:
                        return "⚠ Missing";
                    case ValidationStatus.Failed:
                        return "Needs attention";
                    case ValidationStatus.VersionMismatch:
                        return "⚠ Version Mismatch";
                    case ValidationStatus.Pending:
                        return "Not checked";
                    case ValidationStatus.Running:
                        return "Checking...";
                }
            }
            return "Unknown";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
