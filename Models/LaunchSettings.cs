namespace BeaconSetupManager.Models
{
    public class LaunchSettings
    {
        public string Configuration { get; set; } = "Debug";

        public string ExecutionMode { get; set; } = "BuildAndRun"; // "BuildOnly", "BuildAndRun", "RunOnly"

        public string Environment { get; set; } = "BeaconDev";

        public string Urls { get; set; } = "http://localhost:57663;http://localhost:57664";

        public bool AllowUpgrade { get; set; } = true;

        public string PagesFile { get; set; } = "BeaconAvaClient.pages.json";

        public string AdditionalBackendArguments { get; set; } = "";

        public string AdditionalFrontendArguments { get; set; } = "";

        public bool RememberSettings { get; set; } = true;
    }
}
