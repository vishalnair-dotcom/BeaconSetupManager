# Setup manager fixes and verification

For the 2026-10-15 code review follow-up, see [REVIEW_FIXES.md](REVIEW_FIXES.md).

Implemented 2026-10-14 in the BeaconSetupManager workspace. The external Beacon repository and its run.ps1 are unchanged.

## Changes

| Area | Result |
| --- | --- |
| Operation state | One operation at a time; a captured workspace/source/settings snapshot; input changes blocked during execution; workspace checks repeated before execution; validation results discarded after input changes. Workspace file locks prevent concurrent manager instances from mutating the same workspace. |
| Build/Run | Existing local repositories can build/run without remote connectivity or historical setup-manager metadata. Build and Repair are available as actions. All operation exit paths clear busy state; failures/cancellation support retry; database continuation requires a successful build. Database-stage Retry retries database setup. |
| Resume | Explicit completed-step lists, normalized workspace identity, URL/reference/configuration matching, HEAD verification, clean working-tree checks, and hashes of runtime binaries. Older ordinal-only progress is not promoted to verified completion. Database-only progress cannot certify clone/restore/build. |
| Git | Quoted/validated arguments; fetch/checkout/pull/submodule failures propagate; fast-forward-only pulls; short commits and tags supported; origin checked; per-URL validation workspace; worktrees recognized; NUL-delimited status preserves spaced/renamed/untracked paths; recursive submodule diagnostics; merge/rebase/cherry-pick/revert detection; recovery followed by analysis. |
| Local work | Clone failure never deletes existing contents. Start Fresh has guarded targets and confirmation; deletion errors propagate. Linked worktrees, drive roots, and repository links cannot be removed through Start Fresh. Routine directory creation no longer changes an existing parent directory's owner; explicit ownership repair reports failure. |
| Launch scripts | Native parameters checked from the PowerShell AST. The supplied legacy Beacon script is adapted in a temporary private copy, parsed after adaptation, and executed with encoded/splatted parameters. No git checkout of run.ps1, no repository script overwrite, and no script-content logging. Unrecognized/custom legacy cleanup fails explicitly instead of being discarded. |
| Legacy compatibility | Windows PowerShell 5.1 OS detection; checked build exit code; BuildOnly/RunOnly and launch options; backend/frontend/pages artifact checks; occupied-port check; startup readiness timeout; backend-exit monitoring; cleanup around frontend startup failure. Build-exit markers distinguish successful compilation from a later runtime failure. |
| Processes | Output drains before return; serialized stdout/stderr callbacks; cancellation remains an exception; operation-owned Windows jobs stop descendants, including on manager exit. No global kill by frontend name. Monitoring uses reported process IDs. Native launch is suspended until its job owns it. |
| Database | Validated identifiers/port, cancellation tokens, optional explicit existing-role password/owner changes, safely quoted SQL password literals, credential verification before configuration replacement, and final verification of the actual saved connection. Existing partial changes remain available for retry. Schema upgrades are explicitly associated with backend startup. |
| Configuration | Npgsql connection-string builder used throughout, selected SheetKraft entry updated consistently, unrelated entries retained, malformed XML retained, backup before atomic replacement, nested sample copying, link/containment checks, and synchronization into runtime artifacts before RunOnly. |
| Persistence | Atomic JSON replacement with cross-process locking, corruption backup/recovery on write, normalized workspace keys, per-workspace launch preferences, visible write failures, and atomic theme writes. Build dialogs preserve the preferred run execution mode. |
| UI/logs | Masked passwords and no fixed app password; explicit database port and role/owner options; warning brushes in both themes; corrected dependency status; standard-user dependency scans; cancellable tool checks; larger dialog layouts; corrected step-label style precedence; bounded displayed/build output; user-local logs with retention and secret redaction; window-close cancellation and service/logger disposal. |

## Verification

The regression executable in [Tests/Program.cs](Tests/Program.cs) needs no additional test-framework packages. It uses temporary fixtures and local Git repositories, isolated settings, harmless process fixtures, and a WPF window constructed without showing it. It does not clone the actual remote repository or change a live database.

Run:

```powershell
dotnet run --project Tests/BeaconSetupManager.Regression.csproj --no-restore -v minimal
dotnet build BeaconSetupManager.csproj -c Release --no-restore -v minimal
```

In this environment, NuGet network access was unavailable. Restore succeeded from the existing local package cache using an explicit package directory inside the workspace:

```powershell
dotnet restore Tests/BeaconSetupManager.Regression.csproj --packages C:\OneClickBeaconSetup\BeaconSetupManager\artifacts\validation\packages --source C:\Users\QP-202208\.nuget\packages -p:NuGetAudit=false
```

Final verification: **76 regression checks passed** and the updated **Release build succeeded with zero warnings and zero errors**. Output is saved in [regression.log](artifacts/validation/regression.log) and [dependency-scan-fix-build.log](artifacts/validation/dependency-scan-fix-build.log).

The current executable is [artifacts/validation/action-page-fix/BeaconSetupManager.exe](artifacts/validation/action-page-fix/BeaconSetupManager.exe). It was built separately to avoid overwriting a running manager instance.

The Choose Action page now contains four cards in two rows: Setup, Update Branch, Database Setup and Run Application. Build and Repair were removed from the chooser and its selection command. Build Only remains in the launch configuration dialog, and Repair Repository remains in the recovery dialog. This UI change was verified with a Release build with zero warnings and errors; see [action-page-fix-build.log](artifacts/validation/action-page-fix-build.log).

Dependency cards now appear before scanning. Checks run sequentially in the background and publish their starting state and result immediately, with a completed count and progress. Cancellation preserves completed results and returns an unfinished active card to Not checked. Regression checks cover initial visibility, incremental results, progress, cancellation and a subsequent complete scan.

The actual Beacon configuration sample was also inspected for compatibility: its root is connectionStrings and it contains the SheetKraft/Npgsql entry updated by the manager. Other entries are retained.

## Compatibility and remaining integration checks

### Follow-up: process startup failure

The reported launch failure occurred after successful script inspection and syntax checks. The launch invocation uses a substantially longer encoded command. Microsoft's [CreateProcessWithTokenW documentation](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw) specifies a 1,024-character command-line limit for the administrator-to-user path, making length a likely cause; the old log omitted the native error code, so the exact reported failure could not be proven from that log alone.

Long encoded PowerShell commands now use a short bootstrap and a unique child-environment payload. The bootstrap removes its payload variable before executing the original command. Both execution modes use this transport, allowing unelevated tests to verify its behavior. Additional tests confirm the shortened length, retained custom variables, original exit code, and actual Windows error diagnostics for startup failure. Native errors now include their code and system message rather than being logged as -1 with a generic startup message.

- Old setup progress may cause a rebuild once; it is not trusted as proof of completion. Local Run does not require rebuilding merely to create manager metadata.
- Legacy adaptation supports the supplied script's recognized contract. A customized legacy script with additional cleanup or script-root dependencies needs native launch parameters; the manager retains the original and reports that requirement.
- The app now runs dependency checks as a standard user; SDK workload installation still requires an elevated manager. Global dotnet-script installation runs as the interactive user.
- Elevation with a different Windows account fails with a clear instruction to use the signed-in account. Sharing that other account's settings/temp files with the desktop user is intentionally unsupported.
- Native administrator-token launching, alternate desktop/session permissions, high-DPI visual layout, a full Beacon build/run, and live PostgreSQL provisioning/schema upgrades have not been integration-tested here.
- Database changes are not one rollback transaction because CREATE DATABASE and extension/role operations span connections. Cancellation/error reports retain completed changes for retry.
- PostgreSQL connections.config and its backup remain in the application's required file format, including credentials. Password fields are masked and logs are redacted; this does not change Beacon's configuration format.
- Process-ID runtime status requires PID markers from the script. The legacy adapter emits them. A native-contract script should emit the same markers for precise status reporting.
