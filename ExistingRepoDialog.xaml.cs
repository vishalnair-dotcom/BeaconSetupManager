using System;
using System.Windows;
using System.Windows.Input;

namespace BeaconSetupManager
{
    public partial class ExistingRepoDialog : Window
    {
        public ExistingRepoResult Result { get; private set; } = ExistingRepoResult.Cancel;
        public bool CanResume { get; }
        public string ValidationErrors { get; }

        public Visibility ContinueButtonVisibility => CanResume ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ErrorPanelVisibility => CanResume ? Visibility.Collapsed : Visibility.Visible;

        public ExistingRepoDialog(bool canResume, string validationErrors)
        {
            CanResume = canResume;
            ValidationErrors = validationErrors;
            InitializeComponent();
            DataContext = this;
        }

        public ICommand ContinueCommand => new RelayCommand(() =>
        {
            Result = ExistingRepoResult.Continue;
            DialogResult = true;
            Close();
        });

        public ICommand StartFreshCommand => new RelayCommand(() =>
        {
            if (MessageBox.Show("Delete this repository and its uncommitted files for a fresh clone?", "Confirm Start Fresh", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Result = ExistingRepoResult.StartFresh;
            DialogResult = true;
            Close();
        });

        public ICommand CancelCommand => new RelayCommand(() =>
        {
            Result = ExistingRepoResult.Cancel;
            DialogResult = false;
            Close();
        });

        private class RelayCommand : ICommand
        {
            private readonly Action _action;
            public RelayCommand(Action action) => _action = action;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _action();
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }
    }

    public enum ExistingRepoResult
    {
        Continue,
        StartFresh,
        Cancel
    }
}
