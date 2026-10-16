using System;
using System.Windows;
using System.Windows.Media;
using BeaconSetupManager.Models;

namespace BeaconSetupManager
{
    public partial class RepoRecoveryDialog : Window
    {
        public RepoRecoveryAction Result { get; private set; } = RepoRecoveryAction.Cancel;
        private readonly bool _allowStartFresh;
        public Visibility StartFreshVisibility => _allowStartFresh ? Visibility.Visible : Visibility.Collapsed;

        // View model properties for data binding
        public string StatusDescription { get; }
        
        public string RepoHealthIcon { get; }
        public string RepoHealthText { get; }
        public Brush RepoHealthColor { get; }

        public string ConnectionIcon { get; }
        public string ConnectionText { get; }
        public Brush ConnectionColor { get; }

        public string WorkingTreeIcon { get; }
        public string WorkingTreeText { get; }
        public Brush WorkingTreeColor { get; }

        public string SubmodulesIcon { get; }
        public string SubmodulesText { get; }
        public Brush SubmodulesColor { get; }

        public string SetupProgressText { get; }
        public string CurrentBranchText { get; }

        public Visibility ModifiedFilesListVisibility => _result.ModifiedFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string ModifiedFilesListText => string.Join(Environment.NewLine, _result.ModifiedFiles);

        public string RecommendedActionText { get; }

        // Button panels visibility
        public Visibility LocalModificationsPanelVisibility => _result.State == RepositoryState.LocalModifications ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InterruptedSetupPanelVisibility => (_result.State == RepositoryState.SetupInterrupted || _result.State == RepositoryState.SubmodulesUninitialized || _result.State == RepositoryState.SubmoduleInitializationFailed) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility MergeRebasePanelVisibility => _result.State == RepositoryState.MergeRebaseInProgress ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UnsafeRepoPanelVisibility => _result.State == RepositoryState.UnsafeRepository ? Visibility.Visible : Visibility.Collapsed;

        private readonly RepositoryAnalysisResult _result;

        public RepoRecoveryDialog(RepositoryAnalysisResult result, bool allowStartFresh = true)
        {
            _result = result;
            _allowStartFresh = allowStartFresh;
            
            // 1. Build diagnostics dashboard parameters
            if (result.IsHealthy)
            {
                RepoHealthIcon = "✓";
                RepoHealthText = "Healthy (Git workspace active)";
                RepoHealthColor = new SolidColorBrush(Color.FromRgb(0x00, 0xD0, 0x7D));
            }
            else
            {
                RepoHealthIcon = "✗";
                RepoHealthText = "Unhealthy / Metadata issue";
                RepoHealthColor = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            }

            if (result.IsRemoteConnected)
            {
                ConnectionIcon = "✓";
                ConnectionText = "Connected to remote origin";
                ConnectionColor = new SolidColorBrush(Color.FromRgb(0x00, 0xD0, 0x7D));
            }
            else
            {
                ConnectionIcon = "✗";
                ConnectionText = "Remote disconnected / authentication required";
                ConnectionColor = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            }

            if (result.IsWorkingTreeClean)
            {
                WorkingTreeIcon = "✓";
                WorkingTreeText = "Clean (No developer modifications)";
                WorkingTreeColor = new SolidColorBrush(Color.FromRgb(0x00, 0xD0, 0x7D));
            }
            else
            {
                WorkingTreeIcon = "⚠";
                WorkingTreeText = $"Local modifications detected ({result.ModifiedFiles.Count} file(s) modified)";
                WorkingTreeColor = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
            }

            if (result.SubmoduleStatus == "Healthy")
            {
                SubmodulesIcon = "✓";
                SubmodulesText = "Healthy (All submodules checked out)";
                SubmodulesColor = new SolidColorBrush(Color.FromRgb(0x00, 0xD0, 0x7D));
            }
            else if (result.SubmoduleStatus == "Uninitialized")
            {
                SubmodulesIcon = "⚠";
                SubmodulesText = $"Uninitialized ({result.UninitializedSubmodules.Count} submodule(s) missing)";
                SubmodulesColor = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
            }
            else
            {
                SubmodulesIcon = "✗";
                SubmodulesText = $"Failed / Out of sync: {result.SubmoduleStatus}";
                SubmodulesColor = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            }

            SetupProgressText = result.SetupProgressSummary;
            if (string.IsNullOrEmpty(SetupProgressText))
            {
                SetupProgressText = "No previous Setup Manager progress found.";
            }

            CurrentBranchText = result.CurrentBranch;
            if (string.IsNullOrEmpty(CurrentBranchText))
            {
                CurrentBranchText = "N/A";
            }

            // 2. Classify state description and recommendations
            switch (result.State)
            {
                case RepositoryState.LocalModifications:
                    StatusDescription = "You have local modifications in your workspace. Branch checkout or pull operations cannot proceed while these are present.";
                    RecommendedActionText = "Stash Changes (Safe)";
                    break;
                case RepositoryState.SetupInterrupted:
                    StatusDescription = "A previous setup was interrupted after checking out the files. The repository itself is healthy, but the configuration tasks (such as submodules or compilation) are incomplete.";
                    RecommendedActionText = "Resume Setup";
                    break;
                case RepositoryState.SubmodulesUninitialized:
                    StatusDescription = "The repository is cloned, but the submodules are not checked out. The setup is incomplete.";
                    RecommendedActionText = "Resume Setup";
                    break;
                case RepositoryState.SubmoduleInitializationFailed:
                    StatusDescription = "The submodules initialization failed or submodules are out of sync. This typically happens due to network disruptions or credentials errors.";
                    RecommendedActionText = "Repair Repository (Safe)";
                    break;
                case RepositoryState.MergeRebaseInProgress:
                    StatusDescription = "A merge or rebase operation is currently in progress, leaving the working tree in a conflicted state.";
                    RecommendedActionText = "Abort Merge/Rebase (Safe)";
                    break;
                case RepositoryState.UnsafeRepository:
                    StatusDescription = "Git has flagged this repository as unsafe due to directory ownership issues. This prevents normal git tasks from executing.";
                    RecommendedActionText = "Fix Directory Ownership";
                    break;
                default:
                    StatusDescription = "A repository configuration issue was detected in this workspace directory.";
                    RecommendedActionText = "Repair Repository";
                    break;
            }

            InitializeComponent();
            BeaconSetupManager.Services.WindowLayout.FitToWorkArea(this);
            DataContext = this;
        }

        private void Resume_Click(object sender, RoutedEventArgs e)
        {
            Result = RepoRecoveryAction.Resume;
            DialogResult = true;
            Close();
        }

        private void Repair_Click(object sender, RoutedEventArgs e)
        {
            Result = RepoRecoveryAction.Repair;
            DialogResult = true;
            Close();
        }

        private void StartFresh_Click(object sender, RoutedEventArgs e)
        {
            if (!_allowStartFresh) return;
            var confirm = MessageBox.Show(
                "Are you sure you want to start fresh?\n\nThis will completely delete the existing repository directory and run a clean clone. All uncommitted changes will be lost.",
                "Confirm Start Fresh",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm == MessageBoxResult.Yes)
            {
                Result = RepoRecoveryAction.StartFresh;
                DialogResult = true;
                Close();
            }
        }

        private void Stash_Click(object sender, RoutedEventArgs e)
        {
            Result = RepoRecoveryAction.Stash;
            DialogResult = true;
            Close();
        }

        private void Discard_Click(object sender, RoutedEventArgs e)
        {
            // Confirm before executing destructive operations (git reset --hard and git clean -fd)
            var confirm = MessageBox.Show(
                "Are you sure you want to discard all local changes?\n\nThis will permanently delete all uncommitted changes, additions, and modifications. This operation cannot be undone.",
                "Confirm Discard Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Stop);

            if (confirm == MessageBoxResult.Yes)
            {
                Result = RepoRecoveryAction.Discard;
                DialogResult = true;
                Close();
            }
        }

        private void AbortMerge_Click(object sender, RoutedEventArgs e)
        {
            Result = RepoRecoveryAction.AbortMerge;
            DialogResult = true;
            Close();
        }

        private void FixOwnership_Click(object sender, RoutedEventArgs e)
        {
            Result = RepoRecoveryAction.FixOwnership;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = RepoRecoveryAction.Cancel;
            DialogResult = false;
            Close();
        }
    }
}
