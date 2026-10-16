# SQL Server database setup

Implemented 2026-10-16. PostgreSQL remains supported and is the default for a new workspace.

## Behavior

- Select **PostgreSQL** or **SQL Server** in Database Setup, including the database form reached after Setup/build.
- SQL Server accepts a named instance (`localhost\SQLEXPRESS`) or explicit TCP endpoint (`server,1433`). SQL authentication is the only supported SQL Server authentication mode. Administrator and application credentials are separate.
- **Connect to an existing database** opens and queries the database using the supplied application credentials before saving configuration. It does not provision logins/users, change passwords, grant permissions, or require administrator credentials.
- Provisioning creates a SQL login, database, and mapped database user as needed. It grants `db_owner` only in the selected database to support Beacon's automatic schema upgrades. The mode description makes this permission explicit. The manager grants no server role.
- Provisioning stops before making changes when the database already exists unless **Allow configuring an existing SQL database** is selected. That explicit option also supports resuming partial provisioning. Existing login passwords are retained unless **Update existing login / role password** is selected. Disabled/non-SQL logins and mismatched existing user mappings require administrator intervention.
- SQL Server system databases are rejected as application targets. Identifiers and password literals are quoted; existence checks use parameters. Cancellation propagates, and completed changes remain available for retry.
- Connection strings are built using the selected provider. SQL Server removes `providerName` from the `SheetKraft` entry, matching the supplied sample and Beacon's `DbConnectionConfig` default. PostgreSQL restores `providerName="Npgsql"`. Other entries and backup/replacement protections are retained.
- Saved connections restore the database type, address, application credentials, and SQL certificate-trust option. SQL Server's connect-existing checkbox starts unchecked even when settings are restored; selecting that mode is a manual choice. Existing PostgreSQL connections retain their connect-existing default. Changing database type clears entered credentials and mutation options.
- Generated `connections.config` removes the ReportComparer instruction comment from both copied samples and existing files. Unrelated entries/comments are retained, and the backup preserves the original file.
- Run Application validates the actual saved connection using its provider and retains additional saved connection options. Runtime configuration synchronization remains unchanged.
- The prerequisites page scans local PostgreSQL and SQL Server Windows engine services. It reports both installed, PostgreSQL only, SQL Server only, or neither detected, and includes service names and running/stopped states in the card tooltip. SQL Agent/Browser are not mistaken for database engines. Portable installs and SQL LocalDB are outside this service scan. Remote connectivity is still checked during database setup and Run.
- Database setup uses a compact selector row, a separate connect-existing row directly beneath it, and two columns for server/admin details and application credentials. The database form needs no scrolling at the supported default/minimum sizes. An outer-page scrolling fallback keeps controls reachable in exceptionally short display work areas. Geometry checks cover the checkbox position, all fields and provisioning controls for both providers and both setup modes at default and minimum supported window sizes in light/dark themes.

## Encryption and dependencies

Saved SQL Server connections use exactly these five fields, in this order: `Data Source`, `Initial Catalog`, `User ID`, `Password`, and `TrustServerCertificate`. No `Integrated Security`, `Persist Security Info`, `Connect Timeout`, `Encrypt`, or `providerName` is added. Connection checks set their ten-second timeout internally; encryption follows the SQL client's defaults. **Trust SQL Server certificate** defaults to false and must be explicitly enabled for an intentionally trusted local/self-signed certificate. A saved selection is restored. Windows authentication, Azure/Entra authentication, database server installation, restores, and schema scripts are outside this feature.

The manager uses `Microsoft.Data.SqlClient` 7.0.1, restored from the available package cache. SQL Server provisioning uses its client API; no SSMS or `sqlcmd` installation is needed. The published application must include/bundle the SQL client's Windows runtime dependencies; distribute the complete publish output.

## Validation

The database/provider, Git/process/submodule and legacy-launch suite passed **188 regression checks** before the later authentication, progress and layout updates. Subsequent focused runs passed **4 authentication checks** and **66 UI/workflow checks**, including actual SQL option bounds, both database providers and setup modes, narrow source validation, themed dialogs, work-area sizing, progress and cancellation. The latest Release build and Windows x64 framework-dependent publish succeeded. The latest executable is `artifacts/validation/ux-fixes-publish/BeaconSetupManager.exe`; it requires the .NET 9 Desktop Runtime. Logs and generated screenshots remain under artifacts/validation and are excluded from source control.

Regression tests include both provider configurations, special characters, backups, retained connection options, SQL/Windows authentication discrimination, system database rejection, SQL provisioning against a recording database fixture, quoting, explicit existing-database protection, credential failures, mapping conflicts, cancellation, retries, and WPF workflows with a recording verifier. Existing Git/process/legacy-launch checks also run.

No live SQL Server or PostgreSQL instance was modified by the regression tests. A real server smoke test is still required for login policy, administrator privileges, database/user creation, certificate handling, named-instance discovery, and Beacon's actual schema upgrades. Connect-existing verifies connectivity/query execution; it does not certify that a supplied login has every permission Beacon's upgrades may require.

New SQL logins follow SQL Server's password policy (`CHECK_POLICY=ON`); the manager does not weaken the server policy to accept a sample password.

Validation logs are under `artifacts/validation/sql-server-*` and excluded from source control.

## Manager changes integrated

The current workspace also includes optional Visual Studio/TortoiseGit prerequisites and required Node.js, TortoiseGit detection, Visual Studio discovery through vswhere with fallback locations, explicit PostgreSQL port support, and isolated project build settings. Missing optional tools do not increase the required-dependency warning count. The dual-engine database scan and compact database UI remain available. Legacy launch regressions use a checked-in fixture instead of another developer checkout. Combined validation: 162 checks passed; framework-dependent Windows x64 publish succeeded. Logs: `artifacts/validation/manager-merge-regression.log` and `artifacts/validation/manager-merge-publish.log`.
