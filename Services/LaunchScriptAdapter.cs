using System.Text.RegularExpressions;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services;

public static class LaunchScriptAdapter
{
    public static void Validate(LaunchSettings settings)
    {
        if (settings.Configuration is not ("Debug" or "Release")) throw new ArgumentException("Select Debug or Release.");
        if (settings.ExecutionMode is not ("BuildOnly" or "BuildAndRun" or "RunOnly")) throw new ArgumentException("Select a valid execution mode.");
        if (string.IsNullOrWhiteSpace(settings.Environment) || settings.Environment.Any(char.IsControl)) throw new ArgumentException("Enter an environment name.");
        var urls = settings.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls.Length == 0 || urls.Any(url => !Uri.TryCreate(url.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Enter one or more HTTP(S) URLs separated by semicolons.");
        if (string.IsNullOrWhiteSpace(settings.PagesFile) || settings.PagesFile.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("Enter a pages file.");
        CommandArguments.Split(settings.AdditionalBackendArguments);
        CommandArguments.Split(settings.AdditionalFrontendArguments);
    }

    // Adapt a private copy of the known Beacon legacy contract. Unrecognized layouts fail rather than overwriting repository files.
    public static string Adapt(string source)
    {
        source = source.Replace("\r\n", "\n");
        if (!source.Contains("dotnet build BeaconAvaClient.sln -c $Configuration") ||
            !source.Contains("# 4. Launch Backend") || !source.Contains("# 6. Lifecycle Management") ||
            !source.Contains("artifacts/bin/EngineUpgraded/$configPath") || !source.Contains("artifacts/bin/BeaconAvaClient/$configPath"))
            throw new NotSupportedException("This run.ps1 layout is not supported by the legacy adapter. Update the repository script to the launch parameter contract; the original file has been retained.");
        var parameterPattern = @"\A\s*param\s*\(.*?\n\)";
        if (!Regex.IsMatch(source, parameterPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase)) throw new NotSupportedException("The script parameter block is not recognized.");
        const string expectedLifecycle = """
# 6. Lifecycle Management
try {
    Write-Host "`nRunning ($Environment). Close the Frontend window to stop." -ForegroundColor Gray
    $frontendProcess | Wait-Process
} finally {
    Write-Host "`nFrontend closed. Stopping backend (PID: $($backendProcess.Id))..." -ForegroundColor Red
    try {
        Stop-Process -Id $backendProcess.Id -Force -ErrorAction SilentlyContinue
    } catch {}
    Write-Host "Cleanup successful." -ForegroundColor Green
}
""";
        int lifecycleStart = source.IndexOf("# 6. Lifecycle Management", StringComparison.Ordinal);
        if (Regex.Replace(source[lifecycleStart..].Trim(), @"\s+", " ") != Regex.Replace(expectedLifecycle.Trim(), @"\s+", " ") ||
            Regex.Matches(source, @"\$PSScriptRoot\b", RegexOptions.IgnoreCase).Count != 2 ||
            !source.Contains("$frontendProcess = Start-Process -FilePath $frontendExe -ArgumentList $frontendArgs -WorkingDirectory $frontendDir -PassThru"))
            throw new NotSupportedException("This legacy script has custom startup or cleanup logic. Add the native launch parameters before using it with the manager; its original contents have been retained.");
        string parameters = """
param (
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$NoBuild, [switch]$BuildOnly, [string]$Environment,
    [string]$Urls, [switch]$AllowUpgrade, [string]$PagesFile,
    [string]$AdditionalBackendArgs, [string]$AdditionalFrontendArgs
)
if (-not (Get-Variable IsWindows -ErrorAction SilentlyContinue)) { $IsWindows = $env:OS -eq 'Windows_NT' }
$launch = $env:BEACON_LAUNCH_JSON | ConvertFrom-Json
$backendProcess = $null
$frontendProcess = $null
function Quote-NativeArgument([string]$value) {
    $escaped = [regex]::Replace($value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}
""";
        source = Regex.Replace(source, parameterPattern, _ => parameters, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        source = Regex.Replace(source, @"(?m)^\$rootPath\s*=.*$", _ => "$rootPath = $env:BEACON_REPO_ROOT");
        source = source.Replace("dotnet build BeaconAvaClient.sln -c $Configuration", "dotnet build BeaconAvaClient.sln -c $Configuration\n    $buildExit = $LASTEXITCODE\n    Write-Host \"BEACON_BUILD_EXIT:$buildExit\"\n    if ($buildExit -ne 0) { exit $buildExit }");
        var backendArgs = """
$backendArgs = @($backendDll, '--urls', $Urls, '--environment', $Environment)
if ($AllowUpgrade) { $backendArgs += '--allowUpgrade' }
$backendArgs += @($launch.BackendArguments)
$backendArgs = @($backendArgs | ForEach-Object { Quote-NativeArgument $_ })
""";
        var argsPattern = @"\$backendArgs\s*=\s*@\([^)]*\)";
        if (Regex.Matches(source, argsPattern).Count != 1) throw new NotSupportedException("The backend argument block is not recognized.");
        source = Regex.Replace(source, argsPattern, _ => backendArgs);
        source = Regex.Replace(source, @"\$frontendArgs\s*=\s*@\([^)]*\)", _ => "$frontendArgs = @('--pages', $PagesFile) + @($launch.FrontendArguments)\n$frontendArgs = @($frontendArgs | ForEach-Object { Quote-NativeArgument $_ })");
        source = source.Replace("# 4. Launch Backend", """
if ($BuildOnly) { Write-Host 'BuildOnly mode requested. Exiting without launching services.'; exit 0 }
if (-not (Test-Path (Join-Path $backendDir 'SheetKraftWeb.dll'))) { throw 'Backend artifacts are missing. Build first.' }
if (-not (Test-Path (Join-Path $frontendDir 'AvaClient.Desktop.exe'))) { throw 'Frontend artifacts are missing. Build first.' }
$pagesPath = if ([IO.Path]::IsPathRooted($PagesFile)) { $PagesFile } else { Join-Path $frontendDir $PagesFile }
if (-not (Test-Path $pagesPath)) { throw 'The selected pages file is missing.' }
$endpoint = [Uri](($Urls -split ';')[0].Replace('*', 'localhost').Replace('+', 'localhost'))
$endpointHost = $endpoint.DnsSafeHost
$endpointAddress = $null
$endpointFamily = [Net.Sockets.AddressFamily]::InterNetwork
if ([Net.IPAddress]::TryParse($endpointHost, [ref]$endpointAddress)) {
    $endpointFamily = $endpointAddress.AddressFamily
    if ($endpointAddress.Equals([Net.IPAddress]::Any)) { $endpointHost = '127.0.0.1' }
    elseif ($endpointAddress.Equals([Net.IPAddress]::IPv6Any)) { $endpointHost = '::1' }
    else { $endpointHost = $endpointAddress.ToString() }
} elseif ($endpointHost -eq 'localhost') { $endpointHost = '127.0.0.1' }
$probe = [Net.Sockets.TcpClient]::new($endpointFamily)
$occupied = $false
try { $connection = $probe.ConnectAsync($endpointHost, $endpoint.Port); $occupied = $connection.Wait(2000) -and $probe.Connected } catch {} finally { $probe.Dispose() }
if ($occupied) { throw "Backend port $($endpoint.Port) is already in use. Choose another URL or stop the existing listener." }
try {
# 4. Launch Backend
""");
        source = source.Replace("on http://localhost:57663 ...", "on $Urls ...");
        const string backendStart = "$backendProcess = Start-Process -FilePath \"dotnet\" -ArgumentList $backendArgs -WorkingDirectory $backendDir -PassThru";
        if (!source.Contains(backendStart)) throw new NotSupportedException("The backend launch statement is not recognized.");
        source = source.Replace(backendStart, backendStart + " -WindowStyle Hidden" + """

Write-Host "BEACON_BACKEND_PID:$($backendProcess.Id)"
$ready = $false
for ($attempt = 0; $attempt -lt 150; $attempt++) {
    $backendProcess.Refresh()
    if ($backendProcess.HasExited) { throw "Backend exited during startup with code $($backendProcess.ExitCode)." }
    $client = [Net.Sockets.TcpClient]::new($endpointFamily)
    try {
        $connection = $client.ConnectAsync($endpointHost, $endpoint.Port)
        if ($connection.Wait(200) -and $client.Connected) { $ready = $true; break }
    } catch {} finally { $client.Dispose() }
    Start-Sleep -Milliseconds 200
}
if (-not $ready) { throw 'Backend did not become ready within the startup timeout.' }
""");
        int lifecycle = source.IndexOf("# 6. Lifecycle Management", StringComparison.Ordinal);
        source = source[..lifecycle] + """
Write-Host "BEACON_FRONTEND_PID:$($frontendProcess.Id)"
Write-Host "Running ($Environment). Close the Frontend window to stop."
while (-not $frontendProcess.HasExited) {
    $backendProcess.Refresh()
    if ($backendProcess.HasExited) { throw "Backend exited with code $($backendProcess.ExitCode)." }
    Start-Sleep -Milliseconds 200
    $frontendProcess.Refresh()
}
if ($frontendProcess.ExitCode -ne 0) { throw "Frontend exited with code $($frontendProcess.ExitCode)." }
} finally {
    if ($frontendProcess) { Stop-Process -Id $frontendProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($backendProcess) { Stop-Process -Id $backendProcess.Id -Force -ErrorAction SilentlyContinue }
    Write-Host 'Cleanup successful.'
}
""";
        return source;
    }
}
