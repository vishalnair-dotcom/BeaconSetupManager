# Beacon Setup Manager: flow and edge-case review

Reviewed 2026-10-14. This is the historical baseline **before the fixes**. Its source line references refer to that baseline. See [FIXES_AND_VALIDATION.md](FIXES_AND_VALIDATION.md) for the updated implementation and verification.

## Scope and verification

Reviewed the application startup, all service implementations and interfaces, view model workflows and property-change handlers, models, converters, dialog code, XAML behavior, theme dictionaries, and publish profiles. The extra `BeaconSetupManager_jinofocj_wpftmp.csproj` is a generated WPF build artifact, not a separate application.

Verification performed:

- `dotnet build BeaconSetupManager.csproj --no-restore -v minimal`: succeeded, zero warnings and zero errors.
- Parsed all ten application/theme XAML files as XML.
- Compared resource references against resource declarations: three missing warning brush keys.
- Traced command eligibility, execution guards, state transitions, failure returns, cancellation paths, and persistence reads/writes.
- Checked for tests and the target application's `run.ps1`: neither is included in this workspace's source.

Findings below are established from source inspection unless explicitly described as runtime risks. Build success verifies compilation, not correctness of workflows. No clones, database changes, installations, elevation requests, or application launches were performed. Generated `bin`/`obj` output and third-party package implementations are outside the authored source review.

## Architecture and state

`App` registers singleton services and `MainViewModel` through dependency injection. Startup optionally restarts under administrator privileges, loads the saved theme, and opens `MainWindow`. The view model begins silent repository verification during construction; the window separately scans prerequisites on load.

The wizard uses four page numbers: prerequisites, action selection, workspace/source configuration, and execution/results. The action is a string. Six action cases exist in the view model, but the action-selection page exposes only Setup, UpdateBranch, DatabaseSetup, and RunApplication. Build and Repair remain implementation paths without action cards.

Eligibility depends on several independent state variables:

- `RepoValidationStatus`: connectivity of the entered URL.
- `SourceValidationStatus`: branch/commit validation.
- `WorkspaceValidationStatus`: path and repository checks.
- `IsBusy`: shared across unrelated asynchronous operations.
- `SetupSteps`: mutable in-memory completion flags.
- Setup metadata: one last-completed enum value per workspace path.

These values are not tied together by an immutable operation identity or centralized state machine. Their interaction is responsible for several findings below.

## Flow map

| Flow | Actual sequence | Failure/continuation behavior |
| --- | --- | --- |
| Startup | DI, administrator choice, theme, view model background verification, window prerequisite scan | Cancel exits; failed elevation exits; standard-user mode skips prerequisite scans. Background verification can overlap other validation. |
| Prerequisites | VS 2022, .NET 9 SDK, Git/helper, PostgreSQL service, Node, wasm workload, dotnet-script | Missing requirements are advisory. A completed scan enables setup regardless of missing items. Workload/tool buttons run installations in the manager's process context. |
| Repository verification | `git ls-remote <URL> HEAD` | Success is cached for the session. Startup uses noninteractive GCM settings and a five-second token; manual verification has no explicit timeout. |
| Source validation | Verify entered URL; use existing repository or shared metadata-only temporary clone; fetch and resolve branch/full SHA; read metadata | Does not check out files. An existing repository's origin is used for fetch. Full 40-character SHA receives commit handling; other input receives branch/ref handling. |
| Workspace validation | Rooted path, existing folder, write probe, repository/remote or empty-folder checks | Standard actions accept an empty directory. Simple actions require `.git` directory, root `run.ps1`, root `.sln`, and successful Git work-tree command. |
| Setup | Execution guards; repository analysis/recovery; clone; checkout/pull; submodules; `dotnet restore`; configuration dialog; `run.ps1` BuildOnly | Completed steps can be skipped. Metadata is written after each successful step. Successful build pauses for database configuration. Failure returns to results or source page. |
| Existing repository recovery | Analyze work tree/submodules/progress; refresh index and sync submodule URLs; select recovery | Resume marks steps complete from metadata. Repair refreshes/syncs/updates submodules. StartFresh deletes repository. Stash/discard/abort/fix ownership continue without mandatory reanalysis. |
| UpdateBranch | Mark initial steps complete; recovery; checkout/pull; submodules | Offers optional build. Existing completion flags can cause checkout/submodules to be skipped. Declining build marks action successful and offers Run. |
| Build | Launch dialog forced to BuildOnly; generic run workflow | Generic run workflow also requires all setup metadata steps, including database connection verification. |
| Repair | `SetupRepositoryAsync(forcePullLatest: true)`; dialog; `run.ps1` BuildOnly | Shows build results and a Continue to Database Setup button. Success/final-state flags are not finalized consistently. |
| DatabaseSetup | Workspace selection; execution page; database form | Actual database operation still needs repository config samples despite text saying codebase checks are bypassed. |
| Database operation | Copy config samples; update/preserve connections; create role/database; install citext and uuid-ossp; test app credentials | Failure leaves earlier side effects in place. Successful verification writes final metadata and offers Run. |
| RunApplication | Dialog; load setup metadata; incomplete-setup dialog if needed; check script exists/readable/syntax; launcher patch; execute script; monitoring | Incomplete setup redirects to setup or database form. Runtime errors show detailed dialog. Final cleanup stops monitoring and kills frontend processes by name. |
| Cancellation | Cancel shared token; runner stops process and returns cancellation result | Process cancellation is commonly treated as ordinary failure. Config/database creation do not take cancellation tokens. Administrator launcher terminates only the launched parent process. |

## Persistence and external dependencies

Theme, Git configuration, launch settings, and setup progress are stored under `%LOCALAPPDATA%/QuantumPhinance`.

- `theme.txt`: Light/Dark; fallback Dark.
- `git_config.json`: URL, path, branch, selected build config, verification timestamp/result, Git version, repository type.
- `launch_settings.json`: global launch settings; `localPath` arguments are unused.
- `setup_progress.json`: dictionary keyed by trimmed, lowercased workspace path; values include last step, URL, branch, build config, version, timestamp.
- Logs: daily files beneath the executable directory's `Logs` folder.

The manager depends on the selected repository's root `run.ps1`, root solution file, `SheetKraftWeb/ConfigSamples/Runner`, and optional log4net sample. PostgreSQL connectivity defaults to port 5432. Frontend monitoring assumes `AvaClient.Desktop`; backend monitoring probes the first parsable configured URL's port at 127.0.0.1.

## Findings: execution and resume state

1. **Hidden connectivity guard for Build/Run.** `CanExecuteAction` allows simple-path actions based on workspace success. `ExecuteActionAsync` nevertheless requires successful repository URL verification for Build and RunApplication. The simple UI has no repository verification button, and the URL may be unrelated to the chosen local workspace. Offline run/build can therefore be blocked. See `MainViewModel.cs:49`, `:1040`, `:1060`.
2. **Action changes do not revalidate the workspace.** `OnSelectedActionChanged` resets some repository/source state but leaves workspace success intact. A directory accepted as empty for Setup can retain success after switching to Run or DatabaseSetup without the stricter checks. See `:147`, `:3186`.
3. **Completion flags outlive workspace/branch changes.** Neither URL, branch, path, nor action changes reset `SetupSteps`. Setup treats any completed step as an active resume and bypasses the normal execution guards. UpdateBranch skips already completed checkout/submodule steps. Switching targets or rerunning updates can perform the wrong subset of work. See `:609`, `:631`, `:642`, `:1045`, `:1607`.
4. **Metadata is trusted without validating its identity.** Resume/run consult only workspace path and last-completed enum, not saved URL, branch, build configuration, current HEAD, config files, or actual database health. Reusing a folder or changing checkout can reuse invalid completion state. See `:1847`, `:2328`; `GitService.cs:990`.
5. **Standalone database setup can certify all earlier steps.** It writes `ConnectionValidation` as the last completed step even if this manager never cloned, restored, or built that workspace. Run then marks every preceding enum step complete. See `MainViewModel.cs:2212`, `:2333`.
6. **Retry button is unreachable.** `CanRetryExecution` defaults false and is assigned false at execution start; no source sets it true. The Retry visibility condition cannot be satisfied. See `:1043`; `MainWindow.xaml:1833`.
7. **ConfirmBuild lacks operation finalization.** It creates a token and sets busy state, then directly awaits the run workflow without the outer `ExecuteActionAsync` finally. Normal run completion only stops monitoring/cleanup. Busy can remain true and the token remains assigned. See `MainViewModel.cs:1703`, `:2703`.
8. **Run completion does not consistently set finished state.** Normal RunApplication completion does not set `IsExecutionFinished` or clear busy inside the run workflow; its outer wrapper clears busy but only explicitly marks finished for Build/UpdateBranch. Repair similarly leaves success/finished flags unset after its build. See `:1124`, `:2044`, `:2644`.
9. **Stale prompts/results survive new operations.** Execution start clears the DB form but not all old build-result/build-prompt/run-prompt state. Build results hide the execution panel and DB form. Setup/Repair results offer database continuation even after a failed build; the continuation command has no success guard. See `:1099`, `:2921`; `MainWindow.xaml:1257`, `:1425`, `:1727`.
10. **Incomplete setup metadata does not preserve dirty-state priority.** Analysis sets SetupInterrupted from metadata before assigning LocalModifications. A dirty workspace with incomplete metadata can show Resume/Repair instead of Stash/Discard. Resume may skip work based on stale metadata. See `GitService.cs:1253`.
11. **Fresh setup resets initial completed flags.** The no-`.git` recovery branch resets all steps after Setup has premarked verification/workspace complete, so those checks run again. The workspace execution step reports success even when directory creation silently fails. See `MainViewModel.cs:1784`, `:1213`.

## Findings: Git and recovery

12. **Pull failures can be ignored.** `CheckoutAndValidateLocalAsync` assigns `pullResult` but never checks its exit code, then returns success. The older setup/repair path only returns false for fallback checkout/pull failure when the repo was newly cloned; failures on existing repos can continue. See `GitService.cs:486`, `:495`, `:794`.
13. **Git input quoting is inconsistent.** Several checkout, pull, and metadata commands interpolate branch/ref values without quoting or an option delimiter. Inputs with whitespace, quotes, or leading option characters can change command interpretation. The strings are process arguments, not ordinary shell commands; this is argument handling risk, not proof of arbitrary shell execution. See `GitService.cs:26`, `:466`, `:727`, `:794`.
14. **Shared source-validation clone can belong to another URL.** All repositories use the same `validation_temp` path. If `.git` already exists, validation fetches its origin without comparing origin to the entered URL; verifying the entered URL first does not fix that mismatch. See `MainViewModel.cs:922`; `GitService.cs:245`.
15. **Clone failure cleanup can delete preexisting content.** Metadata validation and the older setup service delete the entire supplied path after certain clone failures. They do not prove that contents were created by that invocation. The primary CloneRepositoryAsync path behaves differently and leaves failed clone contents. See `GitService.cs:271`, `:440`, `:958`.
16. **Deletion errors are swallowed.** `CleanUpDirectory` suppresses deletion failures; callers can proceed as if StartFresh succeeded. No root-path/reparse-point guard exists in that method. ExistingRepoDialog StartFresh has no second destructive confirmation, unlike RepoRecoveryDialog. See `GitService.cs:958`; `ExistingRepoDialog.xaml.cs:30`.
17. **Git worktrees are unsupported by path checks.** Multiple checks require `.git` to be a directory. Linked worktrees/submodules with a `.git` file can be rejected or treated as uninitialized. See `MainViewModel.cs:1232`, `:1786`, `:3231`; `GitService.cs:245`.
18. **Repository analysis can appear clean despite failed status commands.** After the initial Git work-tree command, several command exit codes are ignored. If porcelain-v2 fails, no tracked modifications are parsed, and analysis may end Clean. Submodule-status failure can leave status Unknown without blocking the clean state. See `GitService.cs:1109`, `:1141`, `:1201`.
19. **Dirty-file parsing is incomplete.** Porcelain-v2 paths are split on spaces; renamed and spaced paths can be truncated or missed. Untracked files are collected but excluded from cleanliness; changes inside submodules are excluded. Submodule checking is not recursive. Stash omits untracked files, so an untracked overwrite conflict can persist. See `GitService.cs:1141`, `:1199`, `:1322`.
20. **Ownership detection/recovery is incomplete.** Initial analysis matches `unsafe repository` but not the `dubious ownership` phrase checked elsewhere. Ownership repair catches errors and has no success result; recovery proceeds without proving Git now works. See `GitService.cs:1069`; `ProcessRunner.cs:274`; `MainViewModel.cs:1974`.
21. **Merge/rebase coverage is narrower than error messaging.** Analysis/abort inspect merge and rebase markers only; cherry-pick/revert states are not inspected or aborted. Recovery operations generally continue without mandatory health reanalysis. See `GitService.cs:1091`, `:1302`.
22. **Source handling has limits.** Only full 40-character hex is recognized as a SHA. Short SHAs/tags/ref expressions go through branch handling. Metadata-only initial clone does not specifically fetch an otherwise unreachable requested commit. Remote fetch/authentication is required even for existing local commits. See `GitService.cs:290`, `:645`.

## Findings: launch, process lifecycle, and concurrency

23. **Launcher can discard local script edits.** If its three substring checks fail, it synchronously runs `git checkout -- run.ps1`, ignores exit status, and rewrites the script. There is no backup of local edits. This Git command bypasses the runner's user-context handling, output logging, and cancellation. See `AppLauncherService.cs:74`.
24. **Script modification is heuristic.** Parameter parsing counts parentheses without considering comments/strings; replacement depends on exact comment/cleanup markers and regex shapes. The support check does not verify all parameters passed by the launcher. Patching failures are warnings and execution still proceeds. Run syntax checking occurs before this patch, so the executed version has no post-patch syntax validation. See `AppLauncherService.cs:118`; `MainViewModel.cs:2481`.
25. **Launch input handling is incomplete.** Custom environment is unquoted; URL/pages arguments do not escape embedded quotes; additional arguments use a separate quote convention and are later split on plain spaces. A workspace path containing an apostrophe breaks the syntax-check command's single-quoted PowerShell path. There is no input validation in the launch dialog. See `AppLauncherService.cs:34`; `LaunchConfigurationDialog.xaml.cs:175`; `MainViewModel.cs:2484`.
26. **Cancellation is commonly shown as failure.** Runners return `WasCancelled` with exit code -2, while launcher returns only exit code. Setup checks false and runtime shows an error dialog rather than handling cancellation distinctly. Syntax-validation cancellation can be shown as syntax failure. See `ProcessRunner.cs:253`; `WindowsUserProcessLauncher.cs:424`; `MainViewModel.cs:2486`, `:2660`.
27. **Administrator cancellation may leave children.** Native launcher uses `TerminateProcess` on the parent; the standard-user launcher kills the process tree. Residual cleanup only targets frontend name, not backend descendants. Runtime verification is needed to characterize actual surviving children for the target script. See `WindowsUserProcessLauncher.cs:405`, `:537`.
28. **Cleanup can kill unrelated frontend sessions.** `KillResidualProcesses` kills every process named AvaClient.Desktop, with no workspace/PID ownership filter, including after BuildOnly execution. Monitoring similarly accepts any frontend with that name and any listener on the selected port. See `MainViewModel.cs:2832`, `:2845`.
29. **Fast-exit process race and output drain risk.** Both managed runner implementations start the process and enable exit events before attaching their Exited handler, with no final HasExited check. A sufficiently fast process can finish before subscription and leave the completion task pending. They return on exit without waiting for stdout/stderr EOF; final output can be incomplete. These timing-dependent failures require runtime reproduction. See `ProcessRunner.cs:216`, `:236`; `WindowsUserProcessLauncher.cs:500`, `:520`.
30. **Output callbacks share unsynchronized state.** Stdout and stderr invoke the same view-model callback concurrently, updating shared StringBuilders, counters, and build-section flags without synchronization. Native cancellation disposes pipes without awaiting reader completion. Callback exceptions can escape managed output event handlers. See `WindowsUserProcessLauncher.cs:365`; `MainViewModel.cs:1380`, `:2095`, `:2542`.
31. **Validation has no stale-result protection.** Fire-and-forget workspace/startup/repository tasks read mutable fields around awaits; old results can validate a newly entered target. Multiple operations set/clear the same IsBusy flag. Configuration saves likewise have no serialization or atomic replacement. See `MainViewModel.cs:609`, `:642`, `:657`, `:2987`, `:3358`.
32. **Native user context has assumptions.** Elevated execution needs explorer.exe in the current session; it copies the manager's environment rather than explicitly constructing the user's environment. Alternate-admin credentials can therefore mismatch user token, PATH/profile, and settings location. Duplicated pipe handles can leak when launch fails before explicit close. This path needs Windows integration tests. See `WindowsUserProcessLauncher.cs:160`, `:214`, `:280`, `:433`.
33. **No window-close cancellation/disposal path.** MainWindow does not cancel an operation on close or unsubscribe logging. App has no explicit DI/logger disposal or coordinated process shutdown. Child-process behavior on manager exit remains dependent on the launched script and process context. See `MainWindow.xaml.cs`; `App.xaml.cs`.
34. **Build reporting depends on text markers.** Runtime capture requires exact `1. Building`, `2. Launching Backend`, etc. Status can say Succeeded based on no recognized error lines even when script exit code is nonzero. Error/warning counts are matched lines, not compiler totals. Setup/Repair use final exit code but also count matching lines. See `MainViewModel.cs:2549`, `:2619`, `:2863`.

## Findings: database, configuration, settings, and UI

35. **Database cancellation is partial.** Config and role/database/extension methods take no cancellation token; Cancel only becomes effective at final connection validation. Cancellation there is caught as generic connection failure. Earlier mutations are not rolled back. See `MainViewModel.cs:2137`, `:1410`; `DatabaseService.cs:25`.
36. **Existing database objects are not reconciled.** Existing role passwords, login flags, database owner, and grants are not updated. Existence validation is not used by the workflow. A supplied new password may not match an existing role; the failure appears at app connection verification. See `DatabaseService.cs:65`, `:93`.
37. **Configuration can mismatch successful database verification.** Standalone database setup preserves an existing connections.config, even one just copied from the samples. Final verification uses form values rather than loading that retained file. Thus reported success can coexist with a runtime config pointing to another database. See `ConfigService.cs:92`; `MainViewModel.cs:1405`.
38. **Connection-string escaping differs across paths.** DatabaseService uses NpgsqlConnectionStringBuilder; ConfigService and connection validation interpolate strings. Passwords containing semicolons or special connection-string syntax can fail or alter parsed settings. See `ConfigService.cs:203`; `MainViewModel.cs:1405`.
39. **Config setup can remove intentional connection entries.** It preserves only one selected PostgreSQL entry and deletes every other entry, including other PostgreSQL entries. Malformed XML is rewritten cleanly. Copying is nonrecursive, backup failures are advisory, and existing samples are not refreshed. See `ConfigService.cs:47`, `:104`, `:174`, `:211`, `:231`.
40. **No direct schema initialization in database service.** The method creates role/database/extensions only. UI text mentioning applying schema does not correspond to a schema step here; schema upgrade depends on the external backend/script and AllowUpgrade. See `DatabaseService.cs:25`; `MainViewModel.cs:1590`.
41. **Database input limits are asymmetric.** Identifiers allow alphanumeric/underscore only, but no PostgreSQL length check; invalid names throw before SetupDatabaseAsync's try and are caught by the outer view model. Host/credentials are not prevalidated by the form. Verification hardcodes port 5432. See `DatabaseService.cs:35`, `:206`; `MainViewModel.cs:1405`.
42. **Persistence failures are often invisible.** Launch/progress/theme writes swallow errors; JSON progress corruption cannot be repaired by subsequent writes because parsing existing data fails first. Metadata writes are not atomic and have no inter-instance lock. Path keys do not normalize equivalent paths. Launch settings are shared between workspaces. See `GitService.cs:990`, `:1014`; `LaunchSettingsService.cs`.
43. **Prerequisite detection has limits.** PostgreSQL service existence counts Installed even when stopped; remote/server-container PostgreSQL is not covered. VS detection relies on specific paths/registry and can terminate the scan on registry access exceptions. wasm/global tools are detected by text matches. Standard mode leaves prerequisite items in initial Running state while enabling setup. See `PrerequisiteService.cs:190`, `:243`, `:436`; `MainViewModel.cs:315`.
44. **Missing UI resource keys.** WarningAlertBackgroundBrush, WarningAlertBorderBrush, and WarningAlertTextBrush are referenced but absent from both theme dictionaries. XML/resource comparison confirmed this; warning appearance may use defaults or be inconsistent. See `MainWindow.xaml:795`; `Themes/*.xaml`.
45. **Passwords are visible in ordinary TextBoxes.** Both admin and application passwords are displayed as plain text; app password also has a fixed default. Connection config/backup necessarily contain credentials in their present format. Process diagnostics log complete arguments/output and launcher logs full script contents, so user-provided secrets in those inputs can also enter logs. See `MainWindow.xaml:1467`, `:1493`; `MainViewModel.cs:216`; `ProcessRunner.cs:72`.
46. **Log initialization assumes executable-folder write permission.** LogService creates Logs during dependency resolution with no fallback/catch. Installation in an unwritable folder can fail before the main window. Logs and the RichTextBox grow without an explicit retention/item limit. See `LogService.cs:13`; `MainWindow.xaml.cs:80`.
47. **Some visual states are constrained by layout/property precedence.** Local Foreground values on step labels override style-trigger Foreground setters. Recovery/checkout dialog fixed widths compete with large horizontal button panels, and the launch footer has limited room for three buttons plus Remember. These need visual checks at supported DPI/scales; they are layout risks, not tested display failures.
48. **Legacy/dead elements remain.** IBuildService is injected but unused and both implementation methods throw. StartProject appears only as an execution-guard exception, not an action case. Credential approval, DB admin validation, DB existence validation, TortoiseGit validation, and several old run/build properties have no current UI workflow caller. This matters when interpreting names/comments as actual behavior.

## Runtime scenarios still needed for full behavioral confidence

These scenarios were reasoned through from source, not executed against a live target:

- Standard user and elevated user; alternate admin account; explorer absent; UAC cancellation; writable/unwritable install folder.
- Rapidly change URL/path/branch while validation runs; change action without changing path; repeat updates after successful setup.
- Fresh clone; failed/partial clone; matching/different origin; linked worktree; detached HEAD; short/full SHA; tag; spaced/quoted ref input.
- Failed pull; dirty tracked/untracked files; dirty nested submodules; interrupted setup plus dirty tree; active merge/rebase/cherry-pick/revert.
- Missing/corrupt/stale progress JSON; reused folder; external checkout after completed setup; simultaneous manager instances.
- Missing/unreadable/invalid script; local script edits; alternate script formatting; failure after patch; quoted custom environment/arguments.
- Build exits without expected markers; fast child process; large concurrent stdout/stderr; cancellation during clone/build/run; close manager while running.
- Preexisting frontend/backend sessions; port already occupied; alternate URL host; descendant process cleanup in elevated mode.
- Fresh/existing role/database; changed password; stopped/unreachable PostgreSQL; missing privileges; special password characters; preserved config mismatch; cancellation after partial DB mutations.
- Light/Dark warning states; high DPI; long messages; recovery button layout; build failure followed by database continuation.

The code review supports detailed understanding of every implemented flow and identified failure branch. It cannot establish exhaustive runtime correctness or guarantee that no undiscovered edge case exists without controlled integration scenarios.

## Follow-up: actual Beacon run.ps1

The user supplied `C:/Users/QP-202208/Desktop/BeaconCode/BeaconWebdotnet9Upgrade`. Read its root `run.ps1`, root build properties/targets, and relevant EngineUpgraded/BeaconAvaClient build targets. The repository's AGENTS.md was read; its referenced `docs/agents/README.md` and root AGENT-FLOW.md were unavailable. The external repository was not modified. These findings are recorded here because this is the writable setup-manager workspace.

The script is 3,611 bytes and currently accepts only Configuration, NoBuild, and Environment. It builds `BeaconAvaClient.sln`, launches `dotnet SheetKraftWeb.dll` from `artifacts/bin/EngineUpgraded/<configuration>`, launches AvaClient.Desktop from `artifacts/bin/BeaconAvaClient/<configuration>`, waits for frontend exit, and stops the backend in finally. It does not directly call another PowerShell script. Build targets perform code generation and copy SheetKraft backend/frontend output into these artifact directories.

Read-only verification: Windows PowerShell parser reports zero syntax errors; the tool shell is Windows PowerShell 5.1 and `$IsWindows` is undefined. Config sample directory exists at the exact path expected by ConfigService. Debug backend DLL/frontend EXE were absent at the inspected paths; other configurations were not checked. This was not a build or launch test of Beacon.

Additional findings established from the supplied script:

49. **Windows PowerShell host mismatch.** AppLauncherService invokes powershell.exe. The script uses `$IsWindows` to select the .exe extension and skip chmod. In Windows PowerShell 5.1, the undefined variable sends execution into the Linux branch: extensionless frontend path and chmod attempt. Depending on artifacts/tools, frontend startup can fail after the backend has started. The launcher's script patch does not correct this OS detection. See external `run.ps1:61`; `AppLauncherService.cs:64`.
50. **Failed native build does not explicitly stop launch.** `dotnet build` is followed by backend launch without checking `$LASTEXITCODE`. `$ErrorActionPreference = Stop` does not itself provide a checked native build result in this host. A failed build can therefore proceed with stale artifacts or fail later with an unrelated startup error. See external `run.ps1:30`.
51. **Frontend-start failure leaves backend cleanup uncovered.** The backend starts before the frontend, and the lifecycle try/finally begins only after frontend Start-Process. Frontend path, chmod, or startup failure happens outside that finally. The manager's frontend-only residual cleanup cannot stop the backend in this case. See external `run.ps1:53`, `:67`, `:71`, `:74`.
52. **NoBuild checks only backend artifact existence.** It verifies SheetKraftWeb.dll, not frontend executable, pages JSON, runtime dependencies, or startup readiness. A stopped/crashed backend does not prevent frontend launch. The script has no backend readiness wait or backend-exit monitoring. See external `run.ps1:37`, `:53`, `:71`.
53. **Base script has no BuildOnly or configurable launch parameters.** The manager must successfully patch this particular script before passing Urls, AllowUpgrade, PagesFile, AdditionalBackendArgs, AdditionalFrontendArgs, and BuildOnly. It patches backend args, frontend args, and the exact launch/cleanup markers found in this version. The base script always passes --allowUpgrade. These features are supplied by manager mutation, not native capabilities of the supplied version. See external `run.ps1:1`, `:45`; `AppLauncherService.cs:74`.
54. **Database configuration may be copied before it is created or updated.** EngineUpgraded's PostBuild copies root Config into output Config. Manager Setup/Repair builds first and updates root Config later during database setup. If RunOnly is selected afterward, output Config can remain missing or stale because no subsequent build copies the new root file. Whether runtime reads the copied Config or another path requires tracing backend configuration resolution. See external `EngineUpgraded/EngineUpgraded.csproj:260`; `MainViewModel.cs:1435`, `:2137`.
55. **Artifact paths and command-line spaces need integration checks.** Build output paths are relative to the repository; the script's build command assumes its working directory is the root, which the manager supplies. Start-Process receives arrays containing DLL/path values and user arguments; quoted paths and values with spaces need explicit integration coverage. The hardcoded displayed backend URL is not updated by the patch when custom URLs are selected. See external `run.ps1:20`, `:30`, `:41`, `:53`, `:71`.

The supplied script closes the source-availability gap in the original review. Remaining confidence gaps are controlled runtime scenarios, backend configuration/schema behavior, and process behavior under both Windows execution contexts.
