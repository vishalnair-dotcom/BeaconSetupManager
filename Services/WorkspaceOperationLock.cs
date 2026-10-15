using System.IO;

namespace BeaconSetupManager.Services;

internal static class WorkspaceOperationLock
{
    public static IDisposable Acquire(string workspace)
    {
        string directory = Path.Combine(SettingsStore.DirectoryPath, "workspaces", WorkspacePaths.Hash(workspace));
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("Another manager operation is using this workspace. Finish or cancel it before retrying.", ex); }
    }
}
