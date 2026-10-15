using System;
using System.Windows;
using System.Windows.Media;
using BeaconSetupManager.Models;

namespace BeaconSetupManager
{
    public enum SetupIncompleteAction
    {
        Cancel,
        ResumeSetup,
        ContinueDbSetup,
        ContinueConnVerification
    }

    public class IncompleteStepViewModel
    {
        public string Name { get; set; } = string.Empty;
        public string StatusText { get; set; } = string.Empty;
        public string StatusIcon { get; set; } = string.Empty;
        public Brush StatusColor { get; set; } = Brushes.Gray;
        public bool IsFirstIncomplete { get; set; }
        public Brush RowBackground { get; set; } = Brushes.Transparent;
        public Thickness RowBorderThickness { get; set; } = new Thickness(0);
        public Brush RowBorderBrush { get; set; } = Brushes.Transparent;
    }

    public partial class SetupIncompleteDialog : Window
    {
        public SetupIncompleteAction Result { get; private set; } = SetupIncompleteAction.Cancel;
        public string ContextualMessage { get; }
        public string PrimaryButtonContent { get; }
        public System.Collections.Generic.List<IncompleteStepViewModel> DisplaySteps { get; } = new();

        public SetupIncompleteDialog(System.Collections.Generic.IEnumerable<SetupStepItem> steps)
        {
            SetupStepType? firstIncompleteType = null;
            bool foundFirstIncomplete = false;

            foreach (var step in steps)
            {
                bool isCompleted = step.Status == StepStatus.Completed;
                bool isFirstIncomplete = !isCompleted && !foundFirstIncomplete;
                
                if (isFirstIncomplete)
                {
                    firstIncompleteType = step.StepType;
                    foundFirstIncomplete = true;
                }

                var vm = new IncompleteStepViewModel
                {
                    Name = step.Name,
                    StatusText = isCompleted ? "Completed" : "Pending",
                    StatusIcon = isCompleted ? "✓" : "○",
                    StatusColor = isCompleted 
                        ? new SolidColorBrush(Color.FromRgb(0x00, 0xD0, 0x7D)) 
                        : (isFirstIncomplete ? new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00)) : new SolidColorBrush(Color.FromRgb(0x8A, 0x9A, 0xAD))),
                    IsFirstIncomplete = isFirstIncomplete,
                    RowBackground = isFirstIncomplete 
                        ? new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xA5, 0x00)) 
                        : new SolidColorBrush(Colors.Transparent),
                    RowBorderThickness = isFirstIncomplete ? new Thickness(1.5) : new Thickness(0),
                    RowBorderBrush = isFirstIncomplete 
                        ? new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00)) 
                        : new SolidColorBrush(Colors.Transparent)
                };

                DisplaySteps.Add(vm);
            }

            if (firstIncompleteType != null)
            {
                switch (firstIncompleteType.Value)
                {
                    case SetupStepType.DatabaseSetup:
                        ContextualMessage = "Database Setup is still pending.";
                        PrimaryButtonContent = "Continue Database Setup";
                        Result = SetupIncompleteAction.ContinueDbSetup;
                        break;
                    case SetupStepType.ConnectionValidation:
                        ContextualMessage = "Database Connection Verification has not been completed.";
                        PrimaryButtonContent = "Continue Connection Verification";
                        Result = SetupIncompleteAction.ContinueConnVerification;
                        break;
                    default:
                        var incompleteStep = DisplaySteps.Find(x => x.IsFirstIncomplete);
                        string stepName = incompleteStep != null ? incompleteStep.Name : "Setup";
                        ContextualMessage = $"{stepName} has not been completed.";
                        PrimaryButtonContent = "Resume Setup";
                        Result = SetupIncompleteAction.ResumeSetup;
                        break;
                }
            }
            else
            {
                ContextualMessage = "Setup is incomplete.";
                PrimaryButtonContent = "Resume Setup";
                Result = SetupIncompleteAction.ResumeSetup;
            }

            InitializeComponent();
            DataContext = this;
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = SetupIncompleteAction.Cancel;
            DialogResult = false;
            Close();
        }
    }
}
