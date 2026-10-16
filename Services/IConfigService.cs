using System;
using System.Threading.Tasks;

namespace BeaconSetupManager.Services
{
    public interface IConfigService
    {
        Task<bool> SetupConfigurationAsync(
            string repoRoot,
            string dbHost,
            string dbName,
            string dbUser,
            string dbPassword,
            bool isDatabaseSetup = false,
            Action<string>? onProgress = null,
            System.Threading.CancellationToken cancellationToken = default,
            int port = 5432,
            Models.DatabaseEngine engine = Models.DatabaseEngine.PostgreSql,
            bool trustServerCertificate = false);
    }
}
