using System.IO;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services;

public class LaunchSettingsService : ILaunchSettingsService
{
    private static string ConfigPath(string path) => Path.Combine(SettingsStore.DirectoryPath, "workspaces", WorkspacePaths.Hash(path), "launch_settings.json");
    public LaunchSettings Load(string localPath) => string.IsNullOrWhiteSpace(localPath) ? new() : SettingsStore.Read(ConfigPath(localPath), () => new LaunchSettings());
    public void Save(string localPath, LaunchSettings settings) => SettingsStore.Write(ConfigPath(localPath), settings.RememberSettings ? settings : new LaunchSettings { RememberSettings = false });
}
