using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services;

public interface ISqlServerDatabaseService
{
    Task SetupAsync(DatabaseConnectionSettings application, string adminUsername, string adminPassword,
        bool updateExistingLoginPassword, bool configureExistingDatabase, Action<string>? progress, CancellationToken token);
}
