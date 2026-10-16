using System;
using System.Windows;
using System.Windows.Controls;
using BeaconSetupManager.Models;

namespace BeaconSetupManager
{
    public enum LaunchDialogResult
    {
        Cancel,
        Build,
        Run
    }

    public partial class LaunchConfigurationDialog : Window
    {
        private readonly LaunchSettings _settings;

        public LaunchDialogResult Result { get; private set; } = LaunchDialogResult.Cancel;

        public LaunchConfigurationDialog(LaunchSettings settings, string actionType = "Run")
        {
            InitializeComponent();
            BeaconSetupManager.Services.WindowLayout.FitToWorkArea(this);
            _settings = settings;

            if (actionType == "Setup" || actionType == "Repair" || actionType == "Build")
            {
                _settings.ExecutionMode = "BuildOnly";
                
                // Hide execution mode selection
                LblExecutionMode.Visibility = Visibility.Collapsed;
                PanelExecutionMode.Visibility = Visibility.Collapsed;
                
                // Hide Run button
                BtnRun.Visibility = Visibility.Collapsed;
                BtnRun.IsDefault = false;
                BtnBuild.IsDefault = true;
                
                // Rename Build button content
                if (actionType == "Setup")
                    BtnBuild.Content = "Setup";
                else if (actionType == "Repair")
                    BtnBuild.Content = "Repair";
                else
                    BtnBuild.Content = "Build";
            }

            // Load settings into controls
            // Build Configuration
            if (settings.Configuration == "Release")
                CbConfiguration.SelectedIndex = 1;
            else
                CbConfiguration.SelectedIndex = 0;

            // Execution Mode
            if (settings.ExecutionMode == "BuildOnly")
            {
                RbBuildOnly.IsChecked = true;
            }
            else if (settings.ExecutionMode == "RunOnly")
            {
                RbRunOnly.IsChecked = true;
            }
            else
            {
                RbBuildAndRun.IsChecked = true;
            }

            // Environment
            int envIndex = -1;
            for (int i = 0; i < CbEnvironment.Items.Count - 1; i++)
            {
                if (((ComboBoxItem)CbEnvironment.Items[i]).Content.ToString() == settings.Environment)
                {
                    envIndex = i;
                    break;
                }
            }

            if (envIndex >= 0)
            {
                CbEnvironment.SelectedIndex = envIndex;
            }
            else
            {
                CbEnvironment.SelectedIndex = CbEnvironment.Items.Count - 1; // Custom...
                TxtCustomEnvironment.Text = settings.Environment;
            }

            // Backend
            TxtUrls.Text = settings.Urls;
            ChkAllowUpgrade.IsChecked = settings.AllowUpgrade;

            // Advanced
            TxtPagesFile.Text = settings.PagesFile;
            TxtAdditionalBackendArgs.Text = settings.AdditionalBackendArguments;
            TxtAdditionalFrontendArgs.Text = settings.AdditionalFrontendArguments;

            // Footer
            ChkRemember.IsChecked = settings.RememberSettings;
        }

        private void CbConfiguration_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbEnvironment == null) return;

            // Dynamic defaults: Debug -> BeaconDev, Release -> Production
            // Only update dynamically if environment is not set to Custom and is one of the defaults
            string selectedConfig = (CbConfiguration.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Debug";
            string currentEnv = CbEnvironment.SelectedIndex >= 0 
                ? ((CbEnvironment.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "") 
                : "";

            if (selectedConfig == "Debug")
            {
                if (currentEnv == "Production" || string.IsNullOrEmpty(currentEnv))
                {
                    CbEnvironment.SelectedIndex = 0; // BeaconDev
                }
            }
            else if (selectedConfig == "Release")
            {
                if (currentEnv == "BeaconDev" || string.IsNullOrEmpty(currentEnv))
                {
                    CbEnvironment.SelectedIndex = 3; // Production
                }
            }
        }

        private void CbEnvironment_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TxtCustomEnvironment == null) return;

            if (CbEnvironment.SelectedIndex == CbEnvironment.Items.Count - 1)
            {
                TxtCustomEnvironment.Visibility = Visibility.Visible;
            }
            else
            {
                TxtCustomEnvironment.Visibility = Visibility.Collapsed;
            }
        }

        private void Build_Click(object sender, RoutedEventArgs e)
        {
            // Set Execution Mode to BuildOnly
            _settings.ExecutionMode = "BuildOnly";
            SaveInputsToModel();
            if (!ValidateInputs()) return;
            Result = LaunchDialogResult.Build;
            DialogResult = true;
            Close();
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            // Set Execution Mode to selected radio button choice
            if (RbBuildOnly.IsChecked == true)
                _settings.ExecutionMode = "BuildOnly";
            else if (RbRunOnly.IsChecked == true)
                _settings.ExecutionMode = "RunOnly";
            else
                _settings.ExecutionMode = "BuildAndRun";

            SaveInputsToModel();
            if (!ValidateInputs()) return;
            Result = LaunchDialogResult.Run;
            DialogResult = true;
            Close();
        }

        private bool ValidateInputs()
        {
            try { Services.LaunchScriptAdapter.Validate(_settings); return true; }
            catch (ArgumentException ex) { MessageBox.Show(ex.Message, "Launch settings", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        }

        private void SaveInputsToModel()
        {
            _settings.Configuration = (CbConfiguration.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Debug";

            if (CbEnvironment.SelectedIndex == CbEnvironment.Items.Count - 1)
            {
                _settings.Environment = TxtCustomEnvironment.Text ?? "BeaconDev";
            }
            else
            {
                _settings.Environment = (CbEnvironment.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "BeaconDev";
            }

            _settings.Urls = TxtUrls.Text ?? "";
            _settings.AllowUpgrade = ChkAllowUpgrade.IsChecked == true;

            _settings.PagesFile = TxtPagesFile.Text ?? "";
            _settings.AdditionalBackendArguments = TxtAdditionalBackendArgs.Text ?? "";
            _settings.AdditionalFrontendArguments = TxtAdditionalFrontendArgs.Text ?? "";

            _settings.RememberSettings = ChkRemember.IsChecked == true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = LaunchDialogResult.Cancel;
            DialogResult = false;
            Close();
        }
    }
}
