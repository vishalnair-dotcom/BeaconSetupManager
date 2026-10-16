param (
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$NoBuild,

    [Parameter(HelpMessage="Override the ASPNETCORE_ENVIRONMENT.")]
    [string]$Environment
)

$ErrorActionPreference = "Stop"

# 1. Determine the Environment Default
if ([string]::IsNullOrWhiteSpace($Environment)) {
    $Environment = if ($Configuration -eq "Debug") { "BeaconDev" } else { "Production" }
}

# 2. Resolve Paths (CRITICAL: Convert to Lowercase for Linux/Artifacts folder naming)
$configPath = $Configuration.ToLower()
$rootPath = if ([string]::IsNullOrEmpty($PSScriptRoot)) { Get-Location } else { $PSScriptRoot }

# These paths now use the lowercase $configPath
$backendDir = Join-Path $rootPath "artifacts/bin/EngineUpgraded/$configPath"
$frontendDir = Join-Path $rootPath "artifacts/bin/BeaconAvaClient/$configPath"

# 3. Conditional Build
if (-not $NoBuild) {
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "1. Building ($Configuration) for Environment: $Environment" -ForegroundColor Cyan
    Write-Host "==========================================" -ForegroundColor Cyan
    dotnet build BeaconAvaClient.sln -c $Configuration
} else {
    Write-Host "==========================================" -ForegroundColor Gray
    Write-Host "Skipping Build (Using existing $Configuration artifacts)" -ForegroundColor Gray
    Write-Host "Environment: $Environment" -ForegroundColor Gray

    # Check if the DLL actually exists before trying to run it
    if (-not (Test-Path (Join-Path $backendDir "SheetKraftWeb.dll"))) {
        Write-Error "Artifacts not found in $backendDir. Please run without -NoBuild first."
    }
}

# 4. Launch Backend
Write-Host "`n==========================================" -ForegroundColor Yellow
Write-Host "2. Launching Backend (SheetKraftWeb) on http://localhost:57663 ..." -ForegroundColor Yellow
Write-Host "==========================================" -ForegroundColor Yellow

$backendDll = Join-Path $backendDir "SheetKraftWeb.dll"
$backendArgs = @(
    "$backendDll",
    "--urls", "http://localhost:57663;http://localhost:57664",
    "--environment", "$Environment",
	"--allowUpgrade"
)

$backendProcess = Start-Process -FilePath "dotnet" -ArgumentList $backendArgs -WorkingDirectory $backendDir -PassThru

# 5. Launch Frontend
Write-Host "`n==========================================" -ForegroundColor Green
Write-Host "3. Launching Frontend (AvaClient.Desktop) ..." -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green

# Handle .exe for Windows vs No extension for Linux
$exeExt = if ($IsWindows) { ".exe" } else { "" }
$frontendExe = Join-Path $frontendDir "AvaClient.Desktop$exeExt"

# Fix permissions for Linux native binaries
if (-not $IsWindows) {
    if (Test-Path $frontendExe) { chmod +x $frontendExe }
}

$frontendArgs = @("--pages", "BeaconAvaClient.pages.json")
$frontendProcess = Start-Process -FilePath $frontendExe -ArgumentList $frontendArgs -WorkingDirectory $frontendDir -PassThru

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