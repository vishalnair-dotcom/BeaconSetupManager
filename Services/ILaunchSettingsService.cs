using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services
{
    public interface ILaunchSettingsService
    {
        LaunchSettings Load(string localPath);
        void Save(string localPath, LaunchSettings settings);
    }
}
