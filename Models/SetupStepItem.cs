using CommunityToolkit.Mvvm.ComponentModel;

namespace BeaconSetupManager.Models
{
    public partial class SetupStepItem : ObservableObject
    {
        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private StepStatus _status;

        public SetupStepType StepType { get; }

        public SetupStepItem(SetupStepType stepType, string name, StepStatus status = StepStatus.Pending)
        {
            StepType = stepType;
            _name = name;
            _status = status;
        }
    }
}
