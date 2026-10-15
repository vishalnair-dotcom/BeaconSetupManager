# Code review follow-up

Updated 2026-10-15. This follow-up addresses the four findings from the review of the initial GitHub upload.

## Changes

- **Update Branch recovery:** Start Fresh is hidden and rejected for Update Branch. Setup retains the existing confirmation and guarded deletion/reclone flow. Updates can repair, resume, stash, or explicitly discard changes without deleting the repository as part of Start Fresh.
- **Retry:** Failure and cancellation retain the actual operation and launch settings. Retrying an optional build repeats the build instead of fetching/checking out the branch again. Workspace/source changes invalidate the captured retry. Optional builds use the same workspace validation as other launches, preserve the preferred run mode, and offer Run after successful compilation.
- **Output capture:** Application launches retain at most 4 Mi characters per native output stream. Every line still reaches the logging/UI callback. Git and other callers that require complete machine-readable responses retain their existing full-capture behavior. The view model and displayed log keep their existing limits.
- **Backend probes:** The legacy adapter probes the configured host, normalizes IPv4/IPv6 literals, uses the corresponding socket family, and maps wildcard bind addresses to loopback for probing. The initial occupied-port check allows up to two seconds for connection establishment.
- **Operation startup:** Settings are loaded before acquiring the operation gate, and inputs are checked again after asynchronous validation. A settings-read failure cannot strand the gate, and an invalidated retry cannot start with an obsolete workspace snapshot.

## Verification

Final result: **94 regression checks passed**, including the legacy script and endpoint checks with no skips. The **Release build succeeded with zero warnings and zero errors**. The separate UI-only run passed all 16 UI checks. The final diff also passed Git's whitespace check.

Commands:

```powershell
dotnet run --project Tests/BeaconSetupManager.Regression.csproj --no-restore -v minimal
dotnet build BeaconSetupManager.csproj -c Release --no-restore -v minimal -o artifacts/validation/review-fixes
```

The harness includes real local Git repositories and submodule recovery, output streaming/capture in both process execution modes, generated PowerShell endpoint probes against a listener bound to 127.0.0.2, wildcard/IPv6 host mappings, and WPF build/retry/recovery workflows. Local submodule fixtures use a registered submodule with its own Git directory so creating the fixture does not require rewriting a protected `.git` pointer file. No real Beacon clone, build, database provisioning, or schema upgrade is performed.

The UI checks can also be run separately with `-- --ui-only`. Endpoint and legacy launch integration checks use the separately installed Beacon legacy script when it is available; the harness reports a skip when it is absent.

Logs are written during this validation to:

- `artifacts/validation/review-fixes-regression.log`
- `artifacts/validation/review-fixes-ui.log`
- `artifacts/validation/review-fixes-build.log`

The Release executable is `artifacts/validation/review-fixes/BeaconSetupManager.exe`.

## Remaining integration scope

Real elevated-to-interactive-user launching, a complete Beacon application session, and live PostgreSQL provisioning/schema upgrades still require validation in the intended deployment environment. The process-context regression checks exercise the available Windows session; they do not establish behavior under an elevated administrator token. These fixes address the identified defects and do not constitute a guarantee that the application has no other bugs.
