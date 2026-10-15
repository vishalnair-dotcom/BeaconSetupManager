using System;

namespace BeaconSetupManager.Models
{
    public class SetupMetadata
    {
        public SetupStepType LastCompletedStep { get; set; }
        public DateTime Timestamp { get; set; }
        public string RepoUrl { get; set; } = string.Empty;
        public string BranchOrCommit { get; set; } = string.Empty;
        public string SetupVersion { get; set; } = string.Empty;
        public string BuildConfig { get; set; } = string.Empty;
        public int FormatVersion { get; set; } = 2;
        public string CommitSha { get; set; } = string.Empty;
        public string BuildArtifactsHash { get; set; } = string.Empty;
        public string WorkspacePath { get; set; } = string.Empty;
        public System.Collections.Generic.List<SetupStepType> CompletedSteps { get; set; } = new();
    }
}
