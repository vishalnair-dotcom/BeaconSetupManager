# BeaconSetupManager

A Windows desktop application for setting up and running Beacon workspaces. Built with WPF and .NET 9.

The application checks installed prerequisites and provides four main actions: Setup, Update Branch, Database Setup and Run Application. Build Only is available in launch configuration, and repository recovery is available when needed.

## Build and run

Use Windows with the .NET 9 SDK installed. Visual Studio 2022 with the .NET desktop development workload can also open the project.

```powershell
dotnet restore BeaconSetupManager.csproj
dotnet build BeaconSetupManager.csproj -c Release
dotnet run --project BeaconSetupManager.csproj
```

Beacon itself is a separate repository. Configure its repository URL and workspace path in the application; access to private sources requires your own credentials. Its run.ps1 must support the launch contract or the recognized legacy script format.

## Regression checks

```powershell
dotnet run --project Tests/BeaconSetupManager.Regression.csproj
```

The harness uses isolated local fixtures under artifacts/validation. Some integration checks use the separately installed Beacon legacy script when it is available. Full Beacon execution, administrator-token launching and live PostgreSQL provisioning require additional integration validation.

## Publish

```powershell
dotnet publish BeaconSetupManager.csproj -c Release -p:PublishProfile=win-x64-selfcontained
```

Source control excludes generated builds, test artifacts, logs and local settings. Keep credentials out of committed source files. See FIXES_AND_VALIDATION.md for implementation and validation notes; CODE_FLOW_REVIEW.md records the historical review before fixes.
