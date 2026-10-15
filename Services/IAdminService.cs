namespace BeaconSetupManager.Services
{
    public interface IAdminService
    {
        bool IsRunningAsAdmin();
        void RestartAsAdminAndExit();
    }
}
