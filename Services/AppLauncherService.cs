using System.IO;
using System.Text.Json;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services;

public class AppLauncherService(IProcessRunner runner, ILogService log) : IAppLauncherService
{
    public async Task<int> RunScriptAsync(string repoRoot, LaunchSettings settings, Action<string> onOutputLine, CancellationToken cancellationToken)
    {
        LaunchScriptAdapter.Validate(settings);
        repoRoot = WorkspacePaths.Normalize(repoRoot);
        string original = Path.Combine(repoRoot, "run.ps1");
        string source = await File.ReadAllTextAsync(original, cancellationToken);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "BeaconSetupManager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            // Native contract scripts stay authoritative; adapt only the known legacy Beacon script in a private copy.
            string[] required = ["BuildOnly", "NoBuild", "Configuration", "Environment", "Urls", "AllowUpgrade", "PagesFile", "AdditionalBackendArgs", "AdditionalFrontendArgs"];
            string inspect = "$errors=$null; $ast=[System.Management.Automation.Language.Parser]::ParseFile(" + CommandArguments.PowerShellLiteral(original) + ", [ref]$null, [ref]$errors); if ($errors) { $errors | ForEach-Object { Write-Output $_.Message }; exit 1 }; $ast.ParamBlock.Parameters | ForEach-Object { Write-Output $_.Name.VariablePath.UserPath }";
            var contract = await runner.RunAsync("powershell.exe", CommandArguments.EncodedPowerShell(inspect), repoRoot, cancellationToken: cancellationToken);
            if (contract.ExitCode != 0) throw new InvalidOperationException("Repository startup script syntax is invalid: " + contract.StandardOutput + contract.StandardError);
            var declared = contract.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool legacy = required.Any(name => !declared.Contains(name));
            string script = original;
            if (legacy)
            {
                script = Path.Combine(temporaryDirectory, "run.ps1");
                await File.WriteAllTextAsync(script, LaunchScriptAdapter.Adapt(source), cancellationToken);
                log.LogInfo("Application Launcher", "Using a compatible private copy of the legacy startup script. Repository script retained.");
            }
            string syntax = "$errors = $null; $null = [System.Management.Automation.Language.Parser]::ParseFile(" + CommandArguments.PowerShellLiteral(script) + ", [ref]$null, [ref]$errors); if ($errors) { $errors | ForEach-Object { Write-Output $_.Message }; exit 1 }";
            var validation = await runner.RunAsync("powershell.exe", CommandArguments.EncodedPowerShell(syntax), repoRoot, cancellationToken: cancellationToken);
            if (validation.ExitCode != 0) throw new InvalidOperationException("Startup script syntax validation failed: " + validation.StandardOutput + validation.StandardError);
            SynchronizeRuntimeConfiguration(repoRoot, settings.Configuration);
            var json = JsonSerializer.Serialize(new { settings.Configuration, settings.Environment, settings.Urls, settings.AllowUpgrade, settings.PagesFile,
                AdditionalBackendArgs = settings.AdditionalBackendArguments, AdditionalFrontendArgs = settings.AdditionalFrontendArguments,
                BackendArguments = CommandArguments.Split(settings.AdditionalBackendArguments), FrontendArguments = CommandArguments.Split(settings.AdditionalFrontendArguments) });
            string command = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; $s = $env:BEACON_LAUNCH_JSON | ConvertFrom-Json; $p = @{ Configuration=$s.Configuration; Environment=$s.Environment; Urls=$s.Urls; PagesFile=$s.PagesFile; AdditionalBackendArgs=$s.AdditionalBackendArgs; AdditionalFrontendArgs=$s.AdditionalFrontendArgs }; if ($s.AllowUpgrade) { $p.AllowUpgrade=$true }; ";
            if (settings.ExecutionMode == "BuildOnly") command += "$p.BuildOnly=$true; ";
            if (settings.ExecutionMode == "RunOnly") command += "$p.NoBuild=$true; ";
            command += "try { & " + CommandArguments.PowerShellLiteral(script) + " @p; if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE } } catch { Write-Error $_; exit 1 }";
            var result = await runner.RunAsync("powershell.exe", CommandArguments.EncodedPowerShell(command), repoRoot, onOutputLine, cancellationToken,
                environmentVariables: new() { ["BEACON_LAUNCH_JSON"] = json, ["BEACON_REPO_ROOT"] = repoRoot },
                maximumCapturedOutputCharacters: 4 * 1024 * 1024);
            return result.ExitCode;
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(temporaryDirectory)) File.Delete(file);
            Directory.Delete(temporaryDirectory);
        }
    }
    public static void SynchronizeRuntimeConfiguration(string root, string configuration)
    {
        string source = Path.Combine(root, "Config");
        string output = Path.Combine(root, "artifacts", "bin", "EngineUpgraded", configuration.ToLowerInvariant());
        if (!Directory.Exists(source) || !Directory.Exists(output)) return;
        WorkspacePaths.EnsureContainedWithoutLinks(root, source);
        WorkspacePaths.EnsureContainedWithoutLinks(root, output);
        foreach (string file in WorkspacePaths.EnumerateSafeFiles(source))
        {
            string destination = Path.Combine(output, "Config", Path.GetRelativePath(source, file));
            WorkspacePaths.EnsureContainedWithoutLinks(root, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }
}
