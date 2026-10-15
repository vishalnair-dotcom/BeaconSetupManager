using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeaconSetupManager.Services
{
    public class ProcessRunner : IProcessRunner
    {
        private readonly IInteractiveProcessLauncher _interactiveProcessLauncher;
        private readonly ILogService _logService;

        public ProcessRunner(IInteractiveProcessLauncher interactiveProcessLauncher, ILogService logService)
        {
            _interactiveProcessLauncher = interactiveProcessLauncher;
            _logService = logService;
        }

        public async Task<ProcessResult> RunAsync(
            string fileName,
            string arguments,
            string workingDirectory = "",
            Action<string>? onOutputLine = null,
            CancellationToken cancellationToken = default,
            string? stdInput = null,
            System.Collections.Generic.Dictionary<string, string>? environmentVariables = null,
            ProcessExecutionContext context = ProcessExecutionContext.CurrentUser,
            int? maximumCapturedOutputCharacters = null)
        {
            _logService.LogInfo("ProcessRunner", $"Starting process: '{fileName}' Directory: '{workingDirectory}'. Context: {context}");

            var stopwatch = Stopwatch.StartNew();
            ProcessResult result;

            if (context == ProcessExecutionContext.CurrentUser)
            {
                try
                {
                    result = await _interactiveProcessLauncher.RunAsCurrentUserAsync(
                        fileName,
                        arguments,
                        workingDirectory,
                        onOutputLine,
                        cancellationToken,
                        stdInput,
                        environmentVariables,
                        maximumCapturedOutputCharacters);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    string friendlyMessage = ex is InteractiveSessionException ? ex.Message : $"Could not execute '{fileName}': {ex.Message}";
                    
                    _logService.LogError("ProcessRunner", $"Failed to run process '{fileName}' as current user. Error: {ex.Message}", ex);

                    result = new ProcessResult(
                        ExitCode: -3,
                        StandardOutput: "",
                        StandardError: friendlyMessage,
                        Duration: stopwatch.Elapsed,
                        WasCancelled: false,
                        ExecutionContext: ProcessExecutionContext.CurrentUser,
                        ProcessId: -1);
                }
            }
            else
            {
                // Run elevated (standard process execution under current elevated context)
                result = await RunStandardProcessAsync(fileName, arguments, workingDirectory, onOutputLine, cancellationToken, stdInput, environmentVariables, maximumCapturedOutputCharacters);
            }

            stopwatch.Stop();

            cancellationToken.ThrowIfCancellationRequested();
            if (result.WasCancelled) throw new OperationCanceledException(cancellationToken);
            LogProcessOutcome(fileName, result);
            return result;
        }

        public bool StartBackground(
            string fileName,
            string arguments,
            string workingDirectory = "",
            ProcessExecutionContext context = ProcessExecutionContext.CurrentUser)
        {
            _logService.LogInfo("ProcessRunner", $"Starting background process: '{fileName}' Directory: '{workingDirectory}'. Context: {context}");

            if (context == ProcessExecutionContext.CurrentUser)
            {
                try
                {
                    bool success = _interactiveProcessLauncher.StartAsCurrentUser(fileName, arguments, workingDirectory, createNoWindow: false, useShellExecute: true);
                    _logService.LogInfo("ProcessRunner", $"Background process '{fileName}' start {(success ? "succeeded" : "failed")}.");
                    return success;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logService.LogError("ProcessRunner", $"Failed to start background process '{fileName}' as current user. Error: {ex.Message}", ex);
                    return false;
                }
            }
            else
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = true,
                        CreateNoWindow = false
                    };
                    Process.Start(psi);
                    _logService.LogInfo("ProcessRunner", $"Background process '{fileName}' start succeeded (elevated).");
                    return true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logService.LogError("ProcessRunner", $"Failed to start background process '{fileName}' as elevated. Error: {ex.Message}", ex);
                    return false;
                }
            }
        }

        private Task<ProcessResult> RunStandardProcessAsync(
            string fileName, string arguments, string workingDirectory, Action<string>? onOutputLine,
            CancellationToken cancellationToken, string? stdInput, Dictionary<string, string>? environmentVariables,
            int? maximumCapturedOutputCharacters)
            => _interactiveProcessLauncher.RunAsManagerAsync(fileName, arguments, workingDirectory, onOutputLine,
                cancellationToken, stdInput, environmentVariables, maximumCapturedOutputCharacters);
        private void LogProcessOutcome(string fileName, ProcessResult result)
        {
            string outcome = result.ExitCode == 0 ? "SUCCESS" : "FAILED";
            if (result.WasCancelled) outcome = "CANCELLED";

            _logService.LogInfo("ProcessRunner", 
                $"Process '{fileName}' finished with status: {outcome}. PID: {result.ProcessId}. " +
                $"Exit Code: {result.ExitCode}. Context: {result.ExecutionContext}. Duration: {result.Duration.TotalMilliseconds:F1}ms");

            if (result.ExitCode != 0 && !string.IsNullOrEmpty(result.StandardError))
            {
                _logService.LogWarning("ProcessRunner", $"Process Error Output:\n{result.StandardError.Trim()}");
            }
        }

        public void EnsureDirectoryExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                if (System.IO.Directory.Exists(path))
                {
                    return;
                }

                // Get interactive token
                IntPtr token = _interactiveProcessLauncher.GetInteractiveUserToken();
                if (token != IntPtr.Zero)
                {
                    using var safeHandle = new Microsoft.Win32.SafeHandles.SafeAccessTokenHandle(token);
                    System.Security.Principal.WindowsIdentity.RunImpersonated(safeHandle, () =>
                    {
                        System.IO.Directory.CreateDirectory(path);
                    });

                }
                else
                {
                    System.IO.Directory.CreateDirectory(path);
                }
            }
            catch (Exception ex)
            {
                _logService.LogWarning("ProcessRunner", $"EnsureDirectoryExists failed for '{path}': {ex.Message}");
                // Standard-user creation needs no borrowed token. Never fall back to an elevated owner.
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) throw new IOException($"Could not create '{path}' as the signed-in user.", ex);
                System.IO.Directory.CreateDirectory(path);
            }
        }

        public bool FixDirectoryOwnership(string path)
        {
            try { EnsureDirectoryOwnerIsInteractiveUser(path); return true; }
            catch { return false; }
        }

        private void EnsureDirectoryOwnerIsInteractiveUser(string path)
        {
            try
            {
                IntPtr token = _interactiveProcessLauncher.GetInteractiveUserToken();
                if (token == IntPtr.Zero) return;

                using var tokenHandle = new Microsoft.Win32.SafeHandles.SafeAccessTokenHandle(token);
                using var identity = new System.Security.Principal.WindowsIdentity(tokenHandle.DangerousGetHandle());
                if (identity.User == null) return;

                var dirInfo = new System.IO.DirectoryInfo(path);
                var sec = System.IO.FileSystemAclExtensions.GetAccessControl(dirInfo);
                sec.SetOwner(identity.User);
                System.IO.FileSystemAclExtensions.SetAccessControl(dirInfo, sec);
                
                _logService.LogInfo("ProcessRunner", $"Set directory owner for '{path}' to {identity.Name}");
            }
            catch (Exception ex)
            {
                _logService.LogWarning("ProcessRunner", $"Could not set directory owner for '{path}': {ex.Message}");
                throw;
            }
        }
    }
}

