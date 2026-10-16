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
            new() { Name = "Visual Studio 2022 Community", RequiredVersion = "17.x", IsOptional = true, DownloadUrl = "https://visualstudio.microsoft.com/downloads/", Status = ValidationStatus.Pending },
            new() { Name = ".NET SDK", RequiredVersion = "9.x", DownloadUrl = "https://dotnet.microsoft.com/download", Status = ValidationStatus.Pending },
            new() { Name = "Git", RequiredVersion = "Any", DownloadUrl = "https://git-scm.com/downloads", Status = ValidationStatus.Pending },
            new() { Name = "Database server", RequiredVersion = "PostgreSQL or SQL Server", IsOptional = true, Status = ValidationStatus.Pending,
                Message = "Use an existing local or remote PostgreSQL / SQL Server. Connectivity is verified during database setup." },
            new() { Name = "Node.js", RequiredVersion = "Any", IsOptional = false, DownloadUrl = "https://nodejs.org/", Status = ValidationStatus.Pending },
            new() { Name = "WebAssembly Tools (wasm-tools)", RequiredVersion = "Any", Status = ValidationStatus.Pending },
            new() { Name = "dotnet-script", RequiredVersion = "Any", Status = ValidationStatus.Pending },
            new() { Name = "TortoiseGit", RequiredVersion = "Any", IsOptional = true, DownloadUrl = "https://tortoisegit.org/download/", Status = ValidationStatus.Pending }
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
