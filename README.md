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

## Database setup

Database Setup supports PostgreSQL and SQL Server. Select the database type and either provision a database or connect to an existing one. SQL Server uses username/password authentication only; enter a server instance such as `localhost\SQLEXPRESS` or a TCP endpoint such as `server,1433`.

Connecting to an existing database verifies the application credentials and saves its connection without provisioning users or changing permissions. Provisioning requires administrator credentials. SQL Server provisioning creates/reuses the application login and database user, and grants `db_owner` in the selected database so Beacon can create/upgrade its schema. Configuring a database that already exists, or replacing an existing login password, requires explicitly selecting the corresponding option. No server administrator role is granted.

SQL Server's **Trust server certificate** option is off by default; enable it only for an intentionally trusted server certificate such as a local development instance. An existing connection's selection is restored when reopening setup. SQL Server configuration follows Beacon's provider-less `SheetKraft` entry; PostgreSQL uses `providerName="Npgsql"`. Both preserve unrelated connection entries and back up `connections.config` before replacing it. Beacon performs schema creation/upgrades on startup; the manager does not install a database server, restore backups, or run schema scripts.

See [SQL_SERVER_SUPPORT.md](SQL_SERVER_SUPPORT.md) for validation and deployment notes.

## Regression checks

```powershell
dotnet run --project Tests/BeaconSetupManager.Regression.csproj
```

The harness uses isolated local fixtures under artifacts/validation and a checked-in legacy launch script. Full Beacon execution, administrator-token launching and live PostgreSQL/SQL Server provisioning require additional integration validation.

Appearance defaults to System on first launch; the selector also offers Light and Dark and remembers the selection. Startup opens Choose action when all required dependencies are installed. Node.js is required; Visual Studio and TortoiseGit are optional. Local database detection is advisory because remote databases are supported. Click any timeline step to navigate while no operation is running; execution still requires its normal validations.

Layouts keep stable font sizes when maximized. Repository columns can shrink, forms have bounded widths, and the database submit action stays close to its fields. Database setup needs no scrolling at the default/minimum supported sizes; exceptionally short display work areas get an outer-page scrolling fallback so controls remain reachable. Dialogs are resizable and sized against their monitor's work area. Shared controls and navigation follow the selected theme. Optional prerequisites and advisory local database detection are visibly labeled.

Fresh clones use depth 1 for a single selected branch or tag, excluding unrelated branches and tags. Submodule initialization also requests depth 1, runs up to four downloads in parallel, and checks out the exact revisions recorded by the parent repository. Checkout fetches the selected reference and reuses it for fast-forward updates rather than fetching twice. Missing history is downloaded only when required to resolve older commits or safely update a branch. Existing full-history repositories retain their history. Depth 1 still downloads all files needed for the selected revision, including applicable Git LFS content.

Validate Source checks exact branch and tag names directly through Git's remote reference advertisement (including Azure DevOps), without cloning or creating a cache. Author/date metadata loads after checkout. Commit IDs use a separate cache with no source checkout: full IDs try a targeted fetch first, while short IDs or servers that reject targeted fetches fall back to advertised history. Repository and source verification allow first-time Git Credential Manager sign-in and MFA to finish without an application-imposed deadline. Use Cancel if authentication or connectivity stalls; all downloads support cancellation. Source is checked again during Setup.

After a fresh clone, the manager checks Git access and can correct the selected folder's owner to the signed-in user if an ownership mismatch prevents access. Existing repositories use the explicit Fix Ownership recovery action. Both older Git's “unsafe repository” and newer “dubious ownership” diagnostics are recognized; no global safe-directory exception is added. A completed clone is retained for recovery.

Submodule updates apply `core.hideDotFiles=false` to that command only, to avoid older Windows Git failing to rewrite hidden `.git` pointer files. Registered submodule pointer files already present in the workspace have only their hidden attribute removed; their contents and access permissions are preserved. Directory links and paths outside the workspace are rejected. A file-access failure gets one serial retry using the retained download; persistent failures retain Git's detailed error.

## Publish

```powershell
dotnet publish BeaconSetupManager.csproj -c Release -p:PublishProfile=win-x64-selfcontained
```

Source control excludes generated builds, test artifacts, logs and local settings. Keep credentials out of committed source files. See FIXES_AND_VALIDATION.md for implementation and validation notes; CODE_FLOW_REVIEW.md records the historical review before fixes.
