using System;
using System.Threading.Tasks;

namespace BeaconSetupManager.Services
{
    public interface IDatabaseService
    {
        Task<bool> TestAdminConnectionAsync(string host, string adminUser, string adminPassword);

        Task<bool> SetupDatabaseAsync(
            string host,
            string adminUser,
            string adminPassword,
            string dbName,
            string dbUser,
            string dbPassword,
            Action<string>? onProgress = null,
            System.Threading.CancellationToken cancellationToken = default,
            int port = 5432,
            bool updateExistingRolePassword = false,
            bool updateExistingDatabaseOwner = false);

        Task<(bool DbExists, bool RoleExists, bool ExtensionsInstalled)> ValidateDatabaseSetupAsync(
            string host,
            string adminUser,
            string adminPassword,
            string dbName,
            string dbUser);
    }
}
