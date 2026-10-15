namespace BeaconSetupManager.Models
{
    public class GitValidationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? SourceType { get; set; } // "Branch" or "Commit SHA"
        public string? ResolvedBranch { get; set; } // e.g. branch name, or list of branches
        public string? CommitSha { get; set; }
        public string? Author { get; set; }
        public string? Date { get; set; }
        public string? Message { get; set; }
        public bool HasCheckoutConflicts { get; set; }
    }
}
