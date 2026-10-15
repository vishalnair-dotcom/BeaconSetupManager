using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using BeaconSetupManager.Models;
using Microsoft.Win32;

namespace BeaconSetupManager.Services
{
    public class PrerequisiteService : IPrerequisiteService
    {
        private readonly IProcessRunner _processRunner;

        public PrerequisiteService(IProcessRunner processRunner)
        {
            _processRunner = processRunner;
        }

        public async Task<List<PrerequisiteItem>> ValidateAllAsync(
            Action<PrerequisiteItem>? onItemChecked = null,
            Action<string>? onProgress = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = new List<PrerequisiteItem>();
            var pending = PrerequisiteItem.CreatePendingItems();
            void BeginCheck(int index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = pending[index];
                item.Status = ValidationStatus.Running;
                onItemChecked?.Invoke(item);
            }

            // 1. Visual Studio 2022 Community
            BeginCheck(0);
            onProgress?.Invoke("Validating Visual Studio 2022 Community...");
            PrerequisiteItem vsItem;
            try { vsItem = ValidateVisualStudio2022(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { vsItem = new PrerequisiteItem { Name = "Visual Studio 2022 Community", Status = ValidationStatus.Failed, Message = "Could not inspect Visual Studio: " + ex.Message }; }
            list.Add(vsItem);
            onItemChecked?.Invoke(vsItem);

            // 2. .NET SDK
            BeginCheck(1);
            onProgress?.Invoke("Validating .NET SDK...");
            var sdkItem = await ValidateDotNetSdkAsync(cancellationToken);
            list.Add(sdkItem);
            onItemChecked?.Invoke(sdkItem);

            // 3. Git
            BeginCheck(2);
            onProgress?.Invoke("Validating Git installation...");
            var gitItem = await ValidateGitAsync(cancellationToken);
            list.Add(gitItem);
            onItemChecked?.Invoke(gitItem);



            // 4. PostgreSQL
            BeginCheck(3);
            onProgress?.Invoke("Validating PostgreSQL installation...");
            var pgItem = ValidatePostgreSQLService();
            list.Add(pgItem);
            onItemChecked?.Invoke(pgItem);

            // 5. Node.js (if applicable)
            BeginCheck(4);
            onProgress?.Invoke("Validating Node.js installation...");
            var nodeItem = await ValidateNodeJSAsync(cancellationToken);
            list.Add(nodeItem);
            onItemChecked?.Invoke(nodeItem);

            // 6. WebAssembly Tools (wasm-tools)
            BeginCheck(5);
            onProgress?.Invoke("Validating WebAssembly Tools (wasm-tools)...");
            var wasmItem = await ValidateWasmToolsAsync(cancellationToken);
            list.Add(wasmItem);
            onItemChecked?.Invoke(wasmItem);

            // 7. dotnet-script
            BeginCheck(6);
            onProgress?.Invoke("Validating dotnet-script tool...");
            var scriptItem = await ValidateDotNetScriptAsync(cancellationToken);
            list.Add(scriptItem);
            onItemChecked?.Invoke(scriptItem);

            cancellationToken.ThrowIfCancellationRequested();
            onProgress?.Invoke("Prerequisite validation completed.");
            return list;
        }

        private async Task<PrerequisiteItem> ValidateGitAsync(System.Threading.CancellationToken cancellationToken)
        {
            var item = new PrerequisiteItem { Name = "Git", RequiredVersion = "Any", DownloadUrl = "https://git-scm.com/downloads" };
            try
            {
                var result = await _processRunner.RunAsync("git", "--version", cancellationToken: cancellationToken);
                if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    string gitVersion = result.StandardOutput.Trim();

                    // Check Git Credential helper
                    var helperResult = await _processRunner.RunAsync("git", "config --global credential.helper", cancellationToken: cancellationToken);
                    string helper = helperResult.StandardOutput.Trim();

                    if (string.IsNullOrEmpty(helper))
                    {
                        item.Status = ValidationStatus.Installed;
                        item.DetectedVersion = gitVersion;
                        item.Message = "Git is installed, but no Credential Helper is configured. Private clones may fail or hang. Consider running: git config --global credential.helper manager";
                    }
                    else
                    {
                        item.Status = ValidationStatus.Installed;
                        item.DetectedVersion = $"{gitVersion} (Helper: {helper})";
                        item.Message = "Git is installed and configured with a Credential Helper.";
                    }
                }
                else
                {
                    item.Status = ValidationStatus.Missing;
                    item.Message = "Git command execution failed. Ensure Git is installed and added to PATH.";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                item.Status = ValidationStatus.Missing;
                item.Message = $"Git is not detected: {ex.Message}";
            }
            return item;
        }



        private async Task<PrerequisiteItem> ValidateNodeJSAsync(System.Threading.CancellationToken cancellationToken)
        {
            var item = new PrerequisiteItem { Name = "Node.js (if applicable)", RequiredVersion = "Any", DownloadUrl = "https://nodejs.org/" };
            try
            {
                var result = await _processRunner.RunAsync("node", "--version", cancellationToken: cancellationToken);
                if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    item.Status = ValidationStatus.Installed;
                    item.DetectedVersion = result.StandardOutput.Trim();
                    item.Message = "Node.js is installed.";
                }
                else
                {
                    item.Status = ValidationStatus.Missing;
                    item.Message = "Node.js command execution failed. Ensure Node.js is installed and added to PATH.";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                item.Status = ValidationStatus.Missing;
                item.Message = $"Node.js is not detected: {ex.Message}";
            }
            return item;
        }

        private async Task<PrerequisiteItem> ValidateDotNetSdkAsync(System.Threading.CancellationToken cancellationToken)
        {
            var item = new PrerequisiteItem { Name = ".NET SDK", RequiredVersion = "9.x", DownloadUrl = "https://dotnet.microsoft.com/download" };
            try
            {
                var result = await _processRunner.RunAsync("dotnet", "--list-sdks", cancellationToken: cancellationToken);
                if (result.ExitCode == 0)
                {
                    var lines = result.StandardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var dotnet9Sdk = lines.FirstOrDefault(l => l.Trim().StartsWith("9.", StringComparison.OrdinalIgnoreCase));
                    if (dotnet9Sdk != null)
                    {
                        item.Status = ValidationStatus.Installed;
                        item.DetectedVersion = dotnet9Sdk.Split('[')[0].Trim();
                        item.Message = ".NET 9 SDK is installed.";
                    }
                    else
                    {
                        var anySdk = lines.FirstOrDefault();
                        if (anySdk != null)
                        {
                            item.Status = ValidationStatus.VersionMismatch;
                            item.DetectedVersion = anySdk.Split('[')[0].Trim();
                            item.Message = ".NET 9 SDK is required, but a different version was detected.";
                        }
                        else
                        {
                            item.Status = ValidationStatus.Missing;
                            item.Message = ".NET 9 SDK is required.";
                        }
                    }
                }
                else
                {
                    item.Status = ValidationStatus.Missing;
                    item.Message = ".NET SDK query failed. Ensure .NET SDK is installed.";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                item.Status = ValidationStatus.Missing;
                item.Message = $".NET SDK check failed: {ex.Message}";
            }
            return item;
        }

        private PrerequisiteItem ValidatePostgreSQLService()
        {
            var item = new PrerequisiteItem { Name = "PostgreSQL", RequiredVersion = "Any", DownloadUrl = "https://www.postgresql.org/download/" };
            try
            {
                var services = ServiceController.GetServices();
                var pgService = services.FirstOrDefault(s =>
                    s.ServiceName.StartsWith("postgresql-", StringComparison.OrdinalIgnoreCase) ||
                    s.ServiceName.Equals("postgresql", StringComparison.OrdinalIgnoreCase) ||
                    s.DisplayName.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase));

                if (pgService != null)
                {
                    item.Status = pgService.Status == ServiceControllerStatus.Running ? ValidationStatus.Installed : ValidationStatus.Failed;
                    item.DetectedVersion = pgService.ServiceName;
                    item.Message = $"PostgreSQL Windows Service detected ('{pgService.DisplayName}'). Status: {pgService.Status}";
                }
                else
                {
                    item.Status = ValidationStatus.Missing;
                    item.Message = "PostgreSQL Windows Service is not installed or not running.";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                item.Status = ValidationStatus.Missing;
                item.Message = $"PostgreSQL Service validation error: {ex.Message}";
            }
            return item;
        }

        private PrerequisiteItem ValidateTortoiseGit()
        {
            var item = new PrerequisiteItem { Name = "TortoiseGit", RequiredVersion = "Any", DownloadUrl = "https://tortoisegit.org/download/" };

            bool isInstalled = CheckRegistryKey(@"SOFTWARE\TortoiseGit", "Directory") ||
                               CheckRegistryKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TortoiseGit_is1", "UninstallString") ||
                               CheckRegistryKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TortoiseGit", "UninstallString");

            if (isInstalled)
            {
                item.Status = ValidationStatus.Installed;
                item.DetectedVersion = "Installed";
                item.Message = "TortoiseGit detected in Windows Registry.";
            }
            else
            {
                item.Status = ValidationStatus.Missing;
                item.Message = "TortoiseGit is not detected in Registry.";
            }
            return item;
        }

        private PrerequisiteItem ValidateVisualStudio2022()
        {
            var item = new PrerequisiteItem { Name = "Visual Studio 2022 Community", RequiredVersion = "17.x", IsOptional = false, DownloadUrl = "https://visualstudio.microsoft.com/downloads/" };

            string? vsPath = null;
            string detectedEdition = "VS 2022";

            // Check standard installation paths first for Community, Professional, and Enterprise
            string[] defaultPaths = new[]
            {
                @"C:\Program Files\Microsoft Visual Studio\2022\Community",
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise"
            };

            foreach (var path in defaultPaths)
            {
                if (Directory.Exists(path))
                {
                    vsPath = path;
                    break;
                }
            }

            // Fallback to registry checks if standard paths do not exist
            if (string.IsNullOrEmpty(vsPath) || !Directory.Exists(vsPath))
            {
                // Check 64-bit registry SxS VS7
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\SxS\VS7"))
                {
                    vsPath = key?.GetValue("17.0") as string;
                }

                // Check 32-bit registry SxS VS7 if not found
                if (string.IsNullOrEmpty(vsPath))
                {
                    using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                        .OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\SxS\VS7"))
                    {
                        vsPath = key?.GetValue("17.0") as string;
                    }
                }
            }

            if (!string.IsNullOrEmpty(vsPath) && Directory.Exists(vsPath))
            {
                if (vsPath.Contains("Community", StringComparison.OrdinalIgnoreCase))
                    detectedEdition = "VS 2022 Community";
                else if (vsPath.Contains("Professional", StringComparison.OrdinalIgnoreCase))
                    detectedEdition = "VS 2022 Professional";
                else if (vsPath.Contains("Enterprise", StringComparison.OrdinalIgnoreCase))
                    detectedEdition = "VS 2022 Enterprise";
                else
                    detectedEdition = "VS 2022 (17.0)";

                item.Status = ValidationStatus.Installed;
                item.DetectedVersion = detectedEdition;
                item.Message = $"{detectedEdition} detected at '{vsPath}'.";
            }
            else
            {
                // Check if any older VS is installed (e.g. 16.0 or 15.0)
                string? olderVsPath = null;
                string olderVersion = "";
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\SxS\VS7"))
                {
                    olderVsPath = key?.GetValue("16.0") as string;
                    if (!string.IsNullOrEmpty(olderVsPath)) olderVersion = "VS 2019 (16.0)";
                    else
                    {
                        olderVsPath = key?.GetValue("15.0") as string;
                        if (!string.IsNullOrEmpty(olderVsPath)) olderVersion = "VS 2017 (15.0)";
                    }
                }

                if (!string.IsNullOrEmpty(olderVsPath) && Directory.Exists(olderVsPath))
                {
                    item.Status = ValidationStatus.VersionMismatch;
                    item.DetectedVersion = olderVersion;
                    item.Message = "Visual Studio 2022 is required, but an older version was detected.";
                }
                else
                {
                    item.Status = ValidationStatus.Missing;
                    item.DetectedVersion = "Not Installed";
                    item.Message = "Visual Studio 2022 is not detected in standard paths or registry.";
                }
            }
            return item;
        }

        private bool CheckRegistryKey(string subKeyPath, string valueName)
        {
            // Check LocalMachine (64-bit and 32-bit)
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var hklmKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(subKeyPath);
                if (hklmKey != null)
                {
                    if (string.IsNullOrEmpty(valueName) || hklmKey.GetValue(valueName) != null)
                        return true;
                }
            }

            // Check CurrentUser (64-bit and 32-bit)
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var hkcuKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view).OpenSubKey(subKeyPath);
                if (hkcuKey != null)
                {
                    if (string.IsNullOrEmpty(valueName) || hkcuKey.GetValue(valueName) != null)
                        return true;
                }
            }

            return false;
        }

        public async Task<(bool Success, string ErrorMessage)> ValidateGitAccessAsync(string repoUrl)
        {
            try
            {
                // Run git ls-remote to check remote accessibility (triggers Git Credential Manager popup)
                var result = await _processRunner.RunAsync("git", $"ls-remote \"{repoUrl}\" HEAD");
                if (result.ExitCode == 0)
                {
                    return (true, string.Empty);
                }
                else
                {
                    string err = string.IsNullOrWhiteSpace(result.StandardError)
                        ? "Access denied or repository not found."
                        : result.StandardError.Trim();
                    return (false, err);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public async Task<bool> ApproveGitCredentialsAsync(string repoUrl, string username, string password)
        {
            if (string.IsNullOrWhiteSpace(repoUrl))
                return false;

            try
            {
                string host = "";
                string protocol = "https";

                if (repoUrl.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = repoUrl.Split('@');
                    if (parts.Length > 1)
                    {
                        host = parts[1].Split(':')[0];
                    }
                }
                else
                {
                    var uri = new Uri(repoUrl);
                    host = uri.Host;
                    protocol = uri.Scheme;
                }

                if (string.IsNullOrEmpty(host))
                {
                    return false;
                }

                // Prepare input for git credential approve
                string stdin = $"protocol={protocol}\nhost={host}\nusername={username}\npassword={password}\n\n";

                // Ensure helper is configured
                var configCheck = await _processRunner.RunAsync("git", "config --global credential.helper");
                if (string.IsNullOrWhiteSpace(configCheck.StandardOutput))
                {
                    await _processRunner.RunAsync("git", "config --global credential.helper manager");
                }

                var result = await _processRunner.RunAsync("git", "credential approve", stdInput: stdin);
                return result.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task<PrerequisiteItem> ValidateWasmToolsAsync(System.Threading.CancellationToken cancellationToken)
        {
            var item = new PrerequisiteItem
            {
                Name = "WebAssembly Tools (wasm-tools)",
                RequiredVersion = "Any",
                DownloadUrl = ""
            };

            try
            {
                var result = await _processRunner.RunAsync("dotnet", "workload list", cancellationToken: cancellationToken);
                if (result.ExitCode == 0)
                {
                    string output = result.StandardOutput ?? "";
                    if (output.Contains("wasm-tools", StringComparison.OrdinalIgnoreCase))
                    {
                        item.Status = ValidationStatus.Installed;
                        item.DetectedVersion = "Installed";
                        item.Message = "WebAssembly Tools workload is installed.";
                    }
                    else
                    {
                        item.Status = ValidationStatus.Missing;
                        item.Message = "WebAssembly Tools workload is missing. Click Install to configure.";
                    }
                }
                else
                {
                    item.Status = ValidationStatus.Missing;
                    item.Message = "Failed to list .NET workloads.";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                item.Status = ValidationStatus.Missing;
                item.Message = $"WebAssembly Tools check failed: {ex.Message}";
            }

            return item;
        }

        private async Task<PrerequisiteItem> ValidateDotNetScriptAsync(System.Threading.CancellationToken cancellationToken)
        {
            var item = new PrerequisiteItem
            {
                Name = "dotnet-script",
                RequiredVersion = "Any",
                DownloadUrl = ""
            };

            try
            {
                var result = await _processRunner.RunAsync("dotnet", "tool list -g", cancellationToken: cancellationToken);
                if (result.ExitCode == 0)
                {
                    string output = result.StandardOutput ?? "";
                    if (output.Contains("dotnet-script", StringComparison.OrdinalIgnoreCase))
                    {
                        var versionResult = await _processRunner.RunAsync("dotnet-script", "--version", cancellationToken: cancellationToken);
                        string version = "Installed";
                        if (versionResult.ExitCode == 0 && !string.IsNullOrEmpty(versionResult.StandardOutput))
                        {
                            version = versionResult.StandardOutput.Trim();
                        }
                        item.Status = ValidationStatus.Installed;
                        item.DetectedVersion = version;
                        item.Message = "dotnet-script global tool is installed.";
                    }
                    else
                    {
                        item.Status = ValidationStatus.Missing;
                        item.Message = "dotnet-script global tool is missing. Click Install to configure.";
                    }
                }
                else
                {
                    item.Status = ValidationStatus.Missing;
                    item.Message = "Failed to list global dotnet tools.";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                item.Status = ValidationStatus.Missing;
                item.Message = $"dotnet-script check failed: {ex.Message}";
            }

            return item;
        }
    }
}

