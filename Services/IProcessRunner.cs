using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeaconSetupManager.Services
{
    public record ProcessResult(
        int ExitCode, 
        string StandardOutput, 
        string StandardError, 
        TimeSpan Duration,
        bool WasCancelled,
        ProcessExecutionContext ExecutionContext,
        int ProcessId);

    public interface IProcessRunner
    {
        Task<ProcessResult> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory = "",
            Action<string>? onOutputLine = null,
            CancellationToken cancellationToken = default,
            string? stdInput = null,
            System.Collections.Generic.Dictionary<string, string>? environmentVariables = null,
            ProcessExecutionContext context = ProcessExecutionContext.CurrentUser,
            int? maximumCapturedOutputCharacters = null);

        bool StartBackground(
            string fileName,
            string arguments,
            string workingDirectory = "",
            ProcessExecutionContext context = ProcessExecutionContext.CurrentUser);

        void EnsureDirectoryExists(string path);
        bool FixDirectoryOwnership(string path);
    }
}
