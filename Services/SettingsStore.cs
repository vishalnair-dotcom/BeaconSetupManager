using System.IO;
using System.Text.Json;

namespace BeaconSetupManager.Services;

public static class SettingsStore
{
    internal static string? DirectoryOverride { get; set; }
    public static string DirectoryPath => DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuantumPhinance");
    public static T Read<T>(string path, Func<T> defaults)
    {
        using var mutex = Acquire(path);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? defaults() : defaults(); }
        catch (JsonException) { return defaults(); }
        catch (IOException) { return defaults(); }
    }
    public static void Write<T>(string path, T value) => Update<T>(path, () => value, _ => value);
    public static void Update<T>(string path, Func<T> defaults, Func<T, T> update)
    {
        using var mutex = Acquire(path);
        T previous;
        try { previous = File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? defaults() : defaults(); }
        catch (JsonException) { File.Copy(path, path + ".corrupt", true); previous = defaults(); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(update(previous), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static LockHandle Acquire(string path)
    {
        var mutex = new Mutex(false, "Local\\BeaconSettings_" + WorkspacePaths.Hash(path));
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new IOException("Settings are being used by another operation."); }
        catch (AbandonedMutexException) { }
        return new LockHandle(mutex);
    }
    private sealed class LockHandle(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
