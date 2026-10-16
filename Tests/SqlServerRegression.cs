using System.Data;
using System.IO;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using BeaconSetupManager.Models;
using BeaconSetupManager.Services;
using Microsoft.Data.SqlClient;
using Npgsql;

internal static class SqlServerRegression
{
    public static void VerifyUi(string scratch, Action<Func<Task>> run, Action<bool, string> check)
    {
        string workspace = Path.Combine(scratch, "sql-workflow");
        Directory.CreateDirectory(Path.Combine(workspace, "SheetKraftWeb", "ConfigSamples", "Runner"));
        var postgres = new RecordingPostgres();
        var sql = new RecordingSql();
        var verifier = new RecordingVerifier();
        var runner = new FakeRunner(); var log = new TestLog();
        using var model = new BeaconSetupManager.ViewModels.MainViewModel(new FakePrerequisites(), new GitService(runner, log), postgres,
            new ConfigService(), log, new FakeAdmin(), new AppLauncherService(runner, log), runner, new FakeSettings(), sql, verifier);
        model.LocalPath = workspace;
        model.SelectedDatabaseEngine = DatabaseEngine.SqlServer;
        model.ConnectToExistingDatabase = true;
        model.DbHost = @"localhost\SQLEXPRESS"; model.DbName = "fixture-app"; model.DbUser = "fixture-login"; model.DbPassword = "fixture-password";
        model.TrustSqlServerCertificate = true;
        run(model.RunDbSetupAsync);
        check(model.ExecutionSuccess && sql.Calls == 0 && postgres.Calls == 0 && verifier.Settings.Count == 2,
            "Connect-existing SQL workflow verifies credentials and saved config without provisioning or administrator credentials");
        string path = Path.Combine(workspace, "Config", "connections.config");
        check(ConfigService.ReadApplicationSettings(workspace).Engine == DatabaseEngine.SqlServer && ConfigService.ReadApplicationSettings(workspace).TrustServerCertificate,
            "SQL workflow saves the provider-less connection and explicit certificate trust");
        string previous = File.ReadAllText(path);
        verifier.CancelNext = true;
        model.DbPassword = "invalid-fixture";
        run(model.RunDbSetupAsync);
        check(!model.ExecutionSuccess && model.CanRetryExecution && File.ReadAllText(path) == previous,
            "Cancelled SQL connection verification leaves the saved configuration intact and offers retry");
        model.ConnectToExistingDatabase = false; model.DbAdminUser = "provisioner"; model.DbAdminPassword = "admin-fixture";
        model.DbPassword = "fixture-password";
        model.ConfigureExistingSqlDatabase = true;
        run(model.RunDbSetupAsync);
        check(model.ExecutionSuccess && sql.Calls == 1 && sql.AllowExisting && postgres.Calls == 0,
            "SQL create workflow dispatches only to SQL provisioning with explicit existing-database permission");
        typeof(BeaconSetupManager.ViewModels.MainViewModel).GetMethod("ShowDatabaseForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(model, new object[] { workspace });
        check(model.IsSqlServer && !model.ConnectToExistingDatabase && model.DbHost == @"localhost\SQLEXPRESS" && model.DbPassword == "fixture-password",
            "Database form restores SQL Server settings without automatically selecting connect-existing mode");
        model.SelectedDatabaseEngine = DatabaseEngine.PostgreSql;
        check(model.DbPassword == "" && model.DbAdminPassword == "" && !model.ConfigureExistingSqlDatabase && !model.TrustSqlServerCertificate,
            "Changing database type clears credentials and provider-specific mutation options");
        model.ConnectToExistingDatabase = true;
        model.DbName = "fixture_pg"; model.DbUser = "fixture_pg_user"; model.DbPassword = "fixture-pg-password";
        run(model.RunDbSetupAsync);
        check(model.ExecutionSuccess && postgres.Calls == 0 && sql.Calls == 1 && ConfigService.ReadApplicationSettings(workspace).Engine == DatabaseEngine.PostgreSql,
            "Connect-existing PostgreSQL workflow remains non-provisioning and restores Npgsql configuration");
    }

    public static async Task RunAsync(string scratch, Action<bool, string> check)
    {
        var sql = new DatabaseConnectionSettings { Engine = DatabaseEngine.SqlServer, Server = @"localhost\SQLEXPRESS", Database = "review-db",
            Username = "app-login", Password = "a;b'c\"d&<e", TrustServerCertificate = true };
        var built = new SqlConnectionStringBuilder(DatabaseConnections.Build(sql));
        string exact = DatabaseConnections.Build(sql with { Password = "fixture-password" });
        check(exact == @"Data Source=localhost\SQLEXPRESS;Initial Catalog=review-db;User ID=app-login;Password=fixture-password;TrustServerCertificate=True",
            "SQL saved connection exactly matches the supplied five-field format and keyword spelling");
        check(built.DataSource == sql.Server && built.InitialCatalog == sql.Database && built.Password == sql.Password && !built.IntegratedSecurity && built.TrustServerCertificate,
            "SQL connection builder preserves instance, SQL credentials and special password characters");
        built = new SqlConnectionStringBuilder(DatabaseConnections.Build(sql with { Server = "server.example,1435" }));
        check(built.DataSource == "server.example,1435", "SQL Server supports an explicit non-default TCP port");
        check(!new SqlConnectionStringBuilder(DatabaseConnections.Build(sql with { TrustServerCertificate = false })).TrustServerCertificate,
            "SQL Server certificate trust is explicit");
        var parsed = DatabaseConnections.Parse(DatabaseConnections.Build(sql), null);
        check(parsed.Engine == DatabaseEngine.SqlServer && parsed.Password == sql.Password, "Missing providerName follows Beacon SQL Server default");
        check(!parsed.ToString().Contains(sql.Password) && !parsed.ToString().Contains("Password="), "Database settings diagnostic text does not expose credentials");
        bool rejectsWindows = false;
        try { DatabaseConnections.Parse("Server=localhost;Database=test;Integrated Security=True", null); } catch (ArgumentException) { rejectsWindows = true; }
        check(rejectsWindows, "Windows authentication cannot enter the SQL-password-only workflow");
        bool rejectsProvider = false;
        try { DatabaseConnections.Parse("Data Source=localhost", "Oracle.DataAccess.Client"); } catch (ArgumentException) { rejectsProvider = true; }
        check(rejectsProvider, "Unknown database providers are rejected instead of assumed to be SQL Server");

        string workspace = Path.Combine(scratch, "sql-config-workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "SheetKraftWeb", "ConfigSamples", "Runner"));
        Directory.CreateDirectory(Path.Combine(workspace, "Config"));
        string file = Path.Combine(workspace, "Config", "connections.config");
        File.WriteAllText(file, "<connectionStrings><!--The ReportComparer connection is required for running ReportComparer on the local machine. A separate database is required.--><!--Keep this unrelated note.--><add name='Other' providerName='Other.Provider' connectionString='retained'/><add name='SheetKraft' providerName='Npgsql' connectionString='Host=localhost;Database=old'/></connectionStrings>");
        var config = new ConfigService();
        check(await config.SetupConfigurationAsync(workspace, sql.Server, sql.Database, sql.Username, sql.Password, engine: sql.Engine, trustServerCertificate: true),
            "SQL Server configuration can replace PostgreSQL configuration");
        var entries = XDocument.Load(file).Root!.Elements("add").ToArray();
        check(entries.Single(e => (string?)e.Attribute("name") == "SheetKraft").Attribute("providerName") == null && ConfigService.ReadApplicationSettings(workspace).Password == sql.Password &&
            (string?)entries.Single(e => (string?)e.Attribute("name") == "Other").Attribute("providerName") == "Other.Provider",
            "SQL configuration removes Npgsql only from SheetKraft and preserves unrelated entries");
        check(ConfigService.ReadApplicationConnection(workspace) == DatabaseConnections.Build(sql) &&
            new DbConnectionStringBuilder { ConnectionString = ConfigService.ReadApplicationConnection(workspace) }.Count == 5,
            "Saved SQL XML contains only the five requested connection fields");
        check(XDocument.Load(file + ".bak").Root!.Elements("add").Any(e => (string?)e.Attribute("providerName") == "Npgsql"), "SQL configuration retains a backup of the PostgreSQL connection");
        check(!XDocument.Load(file).DescendantNodes().OfType<XComment>().Any(c => c.Value.Contains("ReportComparer")) &&
            XDocument.Load(file).DescendantNodes().OfType<XComment>().Any(c => c.Value == "Keep this unrelated note.") &&
            XDocument.Load(file + ".bak").DescendantNodes().OfType<XComment>().Any(c => c.Value.Contains("ReportComparer")),
            "Generated config removes ReportComparer boilerplate while retaining unrelated notes and the original backup");
        check(await config.SetupConfigurationAsync(workspace, "localhost", "postgres-app", "pg-app", sql.Password, port: 5544) &&
            ConfigService.ReadApplicationSettings(workspace).Engine == DatabaseEngine.PostgreSql &&
            new NpgsqlConnectionStringBuilder(ConfigService.ReadApplicationConnection(workspace)).Port == 5544,
            "Switching back to PostgreSQL restores Npgsql and its explicit port");
        var ssl = DatabaseConnections.Parse("Host=localhost;Database=app;Username=app;Password=x;SSL Mode=Require;Search Path=appschema", "Npgsql");
        await using (var preserved = DatabaseConnections.Create(ssl))
            check(new NpgsqlConnectionStringBuilder(preserved.ConnectionString).SearchPath == "appschema", "Run verification preserves additional saved PostgreSQL connection options");
        var sqlOptions = DatabaseConnections.Parse("Server=localhost;Database=app;User ID=app;Password=x;Encrypt=False;Application Name=BeaconFixture", null);
        await using (var preserved = DatabaseConnections.Create(sqlOptions))
            check(new SqlConnectionStringBuilder(preserved.ConnectionString).ApplicationName == "BeaconFixture", "Run verification preserves additional saved SQL connection options");

        foreach (string system in new[] { "master", "model", "msdb", "tempdb", "master " })
        {
            bool rejected = false;
            try { SqlServerDatabaseService.Validate(sql with { Database = system }); } catch (ArgumentException) { rejected = true; }
            check(rejected, "SQL provisioning rejects system database: " + system);
        }

        var fixture = new SqlFixture();
        var service = new SqlServerDatabaseService(fixture.Connect);
        await service.SetupAsync(sql, "provisioner", "admin-fixture", false, false, null, CancellationToken.None);
        check(fixture.Commands.Any(c => c.StartsWith("CREATE LOGIN [app-login]")) && fixture.Commands.Contains("CREATE DATABASE [review-db]") &&
            fixture.Commands.Contains("CREATE USER [app-login] FOR LOGIN [app-login]") && fixture.Commands.Contains("ALTER ROLE [db_owner] ADD MEMBER [app-login]"),
            "New SQL database provisioning creates login, database, mapping and database-scoped upgrade permissions");
        check(!fixture.Commands.Any(c => c.Contains("ALTER SERVER ROLE")) && fixture.Commands.Last() == "SELECT 1", "SQL provisioning verifies application access without granting a server role");
        fixture.Commands.Clear();
        await service.SetupAsync(sql, "provisioner", "admin-fixture", false, true, null, CancellationToken.None);
        check(!fixture.Commands.Any(c => c.StartsWith("CREATE ") || c.StartsWith("ALTER ")), "Explicit SQL provisioning resume preserves existing login password and memberships");
        fixture.Commands.Clear();
        await service.SetupAsync(sql, "provisioner", "admin-fixture", true, true, null, CancellationToken.None);
        check(fixture.Commands.Count(c => c.StartsWith("ALTER LOGIN")) == 1 && !fixture.Commands.Any(c => c.StartsWith("CREATE ")),
            "Explicit SQL login password update does not recreate the existing database or user");

        fixture = new SqlFixture { DatabaseExists = true };
        bool existingRejected = false;
        try { await new SqlServerDatabaseService(fixture.Connect).SetupAsync(sql, "provisioner", "admin-fixture", true, false, null, CancellationToken.None); }
        catch (InvalidOperationException) { existingRejected = true; }
        check(existingRejected && !fixture.Commands.Any(c => c.StartsWith("CREATE ") || c.StartsWith("ALTER ")), "Existing SQL database requires explicit permission before any provisioning changes");

        fixture = new SqlFixture { LoginExists = true, RejectApplicationLogin = true };
        bool wrongPassword = false;
        try { await new SqlServerDatabaseService(fixture.Connect).SetupAsync(sql, "provisioner", "admin-fixture", false, false, null, CancellationToken.None); }
        catch (InvalidOperationException) { wrongPassword = true; }
        check(wrongPassword && !fixture.Commands.Any(c => c.StartsWith("CREATE DATABASE")), "Wrong retained login credentials stop SQL provisioning before database creation");

        fixture = new SqlFixture { DatabaseExists = true, LoginExists = true, UserExists = true, UserMatchesLogin = false };
        bool mappingRejected = false;
        try { await new SqlServerDatabaseService(fixture.Connect).SetupAsync(sql, "provisioner", "admin-fixture", false, true, null, CancellationToken.None); }
        catch (InvalidOperationException) { mappingRejected = true; }
        check(mappingRejected && !fixture.Commands.Any(c => c.StartsWith("ALTER ROLE")), "SQL provisioning does not reassign an unrelated existing database user");

        fixture = new SqlFixture();
        var quoted = sql with { Database = "db]name", Username = "user]name" };
        await new SqlServerDatabaseService(fixture.Connect).SetupAsync(quoted, "provisioner", "admin-fixture", false, false, null, CancellationToken.None);
        check(fixture.Commands.Contains("CREATE DATABASE [db]]name]") && fixture.Commands.Any(c => c.StartsWith("CREATE LOGIN [user]]name]") && c.Contains("''")),
            "SQL DDL safely quotes identifiers and password literals");

        fixture = new SqlFixture();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool wasCancelled = false;
        try { await new SqlServerDatabaseService(fixture.Connect).SetupAsync(sql, "provisioner", "admin-fixture", false, false, null, cancelled.Token); }
        catch (OperationCanceledException) { wasCancelled = true; }
        check(wasCancelled && fixture.Connections.Count == 0, "Cancelled SQL provisioning opens no connection");

        fixture = new SqlFixture();
        using var midway = new CancellationTokenSource();
        fixture.AfterExecute = command => { if (command.StartsWith("CREATE DATABASE")) midway.Cancel(); };
        wasCancelled = false;
        try { await new SqlServerDatabaseService(fixture.Connect).SetupAsync(sql, "provisioner", "admin-fixture", false, false, null, midway.Token); }
        catch (OperationCanceledException) { wasCancelled = true; }
        check(wasCancelled && fixture.DatabaseExists && !fixture.Commands.Any(c => c.StartsWith("CREATE USER")),
            "Cancellation during SQL provisioning retains completed objects and stops subsequent changes");
    }
}

internal sealed class RecordingVerifier : IDatabaseConnectionVerifier
{
    public List<DatabaseConnectionSettings> Settings { get; } = new();
    public bool CancelNext;
    public Task VerifyAsync(DatabaseConnectionSettings settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Settings.Add(settings);
        if (CancelNext) { CancelNext = false; throw new OperationCanceledException(); }
        return Task.CompletedTask;
    }
}
internal sealed class RecordingSql : ISqlServerDatabaseService
{
    public int Calls; public bool AllowExisting;
    public Task SetupAsync(DatabaseConnectionSettings settings, string adminUsername, string adminPassword, bool updateExistingLoginPassword,
        bool configureExistingDatabase, Action<string>? progress, CancellationToken token)
    { Calls++; AllowExisting = configureExistingDatabase; return Task.CompletedTask; }
}
internal sealed class RecordingPostgres : IDatabaseService
{
    public int Calls;
    public Task<bool> TestAdminConnectionAsync(string host, string adminUser, string adminPassword, int port = 5432) => Task.FromResult(true);
    public Task<bool> SetupDatabaseAsync(string host, string adminUser, string adminPassword, string dbName, string dbUser, string dbPassword,
        Action<string>? onProgress = null, CancellationToken cancellationToken = default, int port = 5432, bool updateExistingRolePassword = false, bool updateExistingDatabaseOwner = false)
    { Calls++; return Task.FromResult(true); }
    public Task<(bool DbExists, bool RoleExists, bool ExtensionsInstalled)> ValidateDatabaseSetupAsync(string host, string adminUser, string adminPassword, string dbName, string dbUser, int port = 5432)
        => Task.FromResult((true, true, true));
}

internal sealed class SqlFixture
{
    public bool DatabaseExists, LoginExists, UserExists, IsOwner;
    public bool UserMatchesLogin = true, RejectApplicationLogin;
    public List<string> Commands { get; } = new();
    public Action<string>? AfterExecute;
    public List<DatabaseConnectionSettings> Connections { get; } = new();
    public DbConnection Connect(DatabaseConnectionSettings settings) { Connections.Add(settings); return new FixtureConnection(this, settings); }
    private object Execute(string sql)
    {
        Commands.Add(sql);
        if (sql.Contains("sys.databases")) return DatabaseExists ? 1 : 0;
        if (sql.Contains("sys.server_principals") || sql.Contains("sys.sql_logins")) return LoginExists ? 1 : 0;
        if (sql.Contains("sys.database_principals")) return UserExists && (!sql.Contains("SUSER_SID") || UserMatchesLogin) ? 1 : 0;
        if (sql.Contains("IS_ROLEMEMBER")) return IsOwner ? 1 : 0;
        if (sql.StartsWith("CREATE LOGIN")) LoginExists = true;
        if (sql.StartsWith("CREATE DATABASE")) DatabaseExists = true;
        if (sql.StartsWith("CREATE USER")) UserExists = true;
        if (sql.StartsWith("ALTER ROLE")) IsOwner = true;
        AfterExecute?.Invoke(sql);
        return 1;
    }
    private sealed class FixtureConnection(SqlFixture fixture, DatabaseConnectionSettings settings) : DbConnection
    {
        private ConnectionState _state;
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => settings.Database;
        public override string DataSource => settings.Server;
        public override string ServerVersion => "fixture";
        public override ConnectionState State => _state;
        public override void Open()
        {
            if (fixture.RejectApplicationLogin && settings.Username != "provisioner") throw new InvalidOperationException("Fixture login rejected.");
            _state = ConnectionState.Open;
        }
        public override void Close() => _state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new FixtureCommand(fixture) { Connection = this };
    }
    private sealed class FixtureCommand(SqlFixture fixture) : DbCommand
    {
        private readonly SqlCommand _parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery() => Convert.ToInt32(fixture.Execute(CommandText));
        public override object ExecuteScalar() => fixture.Execute(CommandText);
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => new SqlParameter();
        protected override void Dispose(bool disposing) { if (disposing) _parameters.Dispose(); base.Dispose(disposing); }
    }
}
