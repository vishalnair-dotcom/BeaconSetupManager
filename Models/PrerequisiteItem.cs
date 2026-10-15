namespace BeaconSetupManager.Models
{
    public enum ValidationStatus
    {
        Installed,
        Missing,
        Success,
        Failed,
        Running,
        VersionMismatch,
        Pending
    }

    public class PrerequisiteItem
    {
        public static List<PrerequisiteItem> CreatePendingItems() => new()
        {
            new() { Name = "Visual Studio 2022 Community", RequiredVersion = "17.x", DownloadUrl = "https://visualstudio.microsoft.com/downloads/", Status = ValidationStatus.Pending },
            new() { Name = ".NET SDK", RequiredVersion = "9.x", DownloadUrl = "https://dotnet.microsoft.com/download", Status = ValidationStatus.Pending },
            new() { Name = "Git", RequiredVersion = "Any", DownloadUrl = "https://git-scm.com/downloads", Status = ValidationStatus.Pending },
            new() { Name = "PostgreSQL", RequiredVersion = "Any", DownloadUrl = "https://www.postgresql.org/download/", Status = ValidationStatus.Pending },
            new() { Name = "Node.js (if applicable)", RequiredVersion = "Any", DownloadUrl = "https://nodejs.org/", Status = ValidationStatus.Pending },
            new() { Name = "WebAssembly Tools (wasm-tools)", RequiredVersion = "Any", Status = ValidationStatus.Pending },
            new() { Name = "dotnet-script", RequiredVersion = "Any", Status = ValidationStatus.Pending }
        };

        public string Name { get; set; } = string.Empty;
        public string RequiredVersion { get; set; } = string.Empty;
        public string DetectedVersion { get; set; } = string.Empty;
        public ValidationStatus Status { get; set; } = ValidationStatus.Missing;
        public string Message { get; set; } = string.Empty;
        public bool IsOptional { get; set; }
        public string DownloadUrl { get; set; } = string.Empty;

        public string ButtonText => (Name == "WebAssembly Tools (wasm-tools)" || Name == "dotnet-script") ? "Install" : "Download";
    }
}
