using System.Windows;

namespace BeaconSetupManager
{
    public partial class PowerShellErrorDialog : Window
    {
        public string ErrorTitle { get; }
        public string ErrorMessage { get; }
        public string ErrorDetails { get; }

        public PowerShellErrorDialog(string errorTitle, string errorMessage, string errorDetails)
        {
            ErrorTitle = errorTitle;
            ErrorMessage = errorMessage;
            ErrorDetails = errorDetails;

            InitializeComponent();
            DataContext = this;
        }

        private void ToggleDetails_Click(object sender, RoutedEventArgs e)
        {
            if (DetailsGrid.Visibility == Visibility.Visible)
            {
                DetailsGrid.Visibility = Visibility.Collapsed;
                Height = 280;
            }
            else
            {
                DetailsGrid.Visibility = Visibility.Visible;
                Height = 440;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
