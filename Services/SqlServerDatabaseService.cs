using BeaconSetupManager.Models;
using Microsoft.Data.SqlClient;
using System.Data.Common;

namespace BeaconSetupManager.Services;

public sealed class SqlServerDatabaseService(Func<DatabaseConnectionSettings, DbConnection>? connectionFactory = null) : ISqlServerDatabaseService
{
    private readonly Func<DatabaseConnectionSettings, DbConnection> _connect = connectionFactory ?? (settings => new SqlConnection(DatabaseConnections.Build(settings)));
    private async Task VerifyAsync(DatabaseConnectionSettings settings, CancellationToken token)
    {
        await using var connection = _connect(settings);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = 15;
        await command.ExecuteScalarAsync(token);
    }
    public static void Validate(DatabaseConnectionSettings settings)
    {
        if (settings.Engine != DatabaseEngine.SqlServer) throw new ArgumentException("Select SQL Server.");
        if (string.IsNullOrWhiteSpace(settings.Server) || settings.Server.Any(char.IsControl)) throw new ArgumentException("Enter a SQL Server host, instance, or host,port.");
        foreach (var name in new[] { settings.Database, settings.Username })
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > 128 || name.Any(char.IsControl)) throw new ArgumentException("SQL Server database and login names must contain 1–128 characters without control characters or leading/trailing whitespace.");
        if (new[] { "master", "model", "msdb", "tempdb" }.Contains(settings.Database, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Select an application database, not a SQL Server system database.");
        if (string.IsNullOrEmpty(settings.Password) || settings.Password.Contains('\0')) throw new ArgumentException("Enter an application login password without NUL characters.");
    }

    internal static string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

    public async Task SetupAsync(DatabaseConnectionSettings application, string adminUsername, string adminPassword,
        bool updateExistingLoginPassword, bool configureExistingDatabase, Action<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Validate(application);
        if (string.IsNullOrWhiteSpace(adminUsername) || string.IsNullOrEmpty(adminPassword)) throw new ArgumentException("Enter SQL Server administrator username and password for provisioning.");
        if (string.Equals(application.Username, adminUsername, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use a separate application login and administrator login.");
        var adminSettings = application with { Database = "master", Username = adminUsername, Password = adminPassword };
        await using var admin = _connect(adminSettings);
        await admin.OpenAsync(token);
        async Task<bool> Exists(string sql, string value, DbConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql; command.CommandTimeout = 30;
            var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = value;
            command.Parameters.Add(parameter);
            return Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 0;
        }
        async Task Execute(string sql, DbConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql; command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync(token);
        }
        bool databaseExists = await Exists("SELECT COUNT(*) FROM sys.databases WHERE name=@name", application.Database, admin);
        if (databaseExists && !configureExistingDatabase)
            throw new InvalidOperationException("The database already exists. Choose Connect to existing, or explicitly allow configuring the existing database to resume provisioning.");
        string login = QuoteIdentifier(application.Username), database = QuoteIdentifier(application.Database);
        bool loginExists = await Exists("SELECT COUNT(*) FROM sys.server_principals WHERE name=@name", application.Username, admin);
        if (loginExists)
        {
            if (!await Exists("SELECT COUNT(*) FROM sys.sql_logins WHERE name=@name AND is_disabled=0", application.Username, admin))
                throw new InvalidOperationException("The existing login is disabled or is not a SQL authentication login. Resolve it with the database administrator.");
            if (updateExistingLoginPassword)
                await Execute($"ALTER LOGIN {login} WITH PASSWORD={Literal(application.Password)}", admin);
            // Verify retained credentials before creating the database or changing its users.
            await VerifyAsync(application with { Database = "master" }, token);
        }
        else await Execute($"CREATE LOGIN {login} WITH PASSWORD={Literal(application.Password)}, CHECK_POLICY=ON", admin);
        if (!databaseExists) await Execute($"CREATE DATABASE {database}", admin);
        await using var target = _connect(adminSettings with { Database = application.Database });
        await target.OpenAsync(token);
        if (!await Exists("SELECT COUNT(*) FROM sys.database_principals WHERE name=@name", application.Username, target))
            await Execute($"CREATE USER {login} FOR LOGIN {login}", target);
        else if (!await Exists("SELECT COUNT(*) FROM sys.database_principals WHERE name=@name AND sid=SUSER_SID(@name)", application.Username, target))
            throw new InvalidOperationException("The existing database user belongs to a different login. Ask the database administrator to repair the mapping.");
        if (!await Exists("SELECT CASE WHEN IS_ROLEMEMBER('db_owner', @name)=1 THEN 1 ELSE 0 END", application.Username, target))
            await Execute($"ALTER ROLE [db_owner] ADD MEMBER {login}", target);
        await VerifyAsync(application, token);
        progress?.Invoke("SQL Server database and application login verified. Database owner permissions allow Beacon to create/upgrade its schema at startup. No server administrator role was granted.");
    }
}
