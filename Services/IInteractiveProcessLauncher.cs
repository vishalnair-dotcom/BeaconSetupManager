using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BeaconSetupManager.Services
{
    public interface IInteractiveProcessLauncher
    {
        Task<ProcessResult> RunAsCurrentUserAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            Action<string>? onOutputLine,
            CancellationToken cancellationToken,
            string? stdInput,
            Dictionary<string, string>? environmentVariables);

        Task<ProcessResult> RunAsManagerAsync(
            string fileName, string arguments, string workingDirectory, Action<string>? onOutputLine,
            CancellationToken cancellationToken, string? stdInput, Dictionary<string, string>? environmentVariables);

        bool StartAsCurrentUser(
            string fileName,
            string arguments,
            string workingDirectory,
            bool createNoWindow,
            bool useShellExecute);

        IntPtr GetInteractiveUserToken();
    }
}
