using System;
using System.Threading;
using System.Threading.Tasks;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services
{
    public interface IAppLauncherService
    {
        Task<int> RunScriptAsync(
            string repoRoot,
            LaunchSettings settings,
            Action<string> onOutputLine,
            CancellationToken cancellationToken);
    }
}
