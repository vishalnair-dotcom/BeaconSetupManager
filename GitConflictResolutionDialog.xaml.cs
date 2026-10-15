using System.Windows;
using BeaconSetupManager.Models;

namespace BeaconSetupManager
{
    public partial class GitConflictResolutionDialog : Window
    {
        public GitConflictResolution Result { get; private set; } = GitConflictResolution.Cancel;
        public string ConflictMessage { get; }

        public GitConflictResolutionDialog(string conflictMessage)
        {
            ConflictMessage = conflictMessage;
            InitializeComponent();
            DataContext = this;
        }

        private void Retry_Click(object sender, RoutedEventArgs e)
        {
            Result = GitConflictResolution.RetryNormally;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = GitConflictResolution.Cancel;
            DialogResult = false;
            Close();
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            Result = GitConflictResolution.Merge;
            DialogResult = true;
            Close();
        }

        private void Force_Click(object sender, RoutedEventArgs e)
        {
            Result = GitConflictResolution.Force;
            DialogResult = true;
            Close();
        }
    }
}
