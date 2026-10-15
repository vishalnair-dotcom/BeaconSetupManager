using System.Collections.Generic;

namespace BeaconSetupManager.Models
{
    public class RepositoryAnalysisResult
    {
        public RepositoryState State { get; set; } = RepositoryState.Unknown;
        public bool IsHealthy { get; set; }
        public bool IsRemoteConnected { get; set; }
        public bool IsWorkingTreeClean { get; set; }
        public string CurrentBranch { get; set; } = string.Empty;
        public string SubmoduleStatus { get; set; } = "Unknown";
        public string SetupProgressSummary { get; set; } = "Not Started";
        public int ModifiedFilesCount => ModifiedFiles.Count;
        public string ErrorMessage { get; set; } = string.Empty;
        public List<string> ModifiedFiles { get; set; } = new();
        public List<string> UninitializedSubmodules { get; set; } = new();
    }
}
