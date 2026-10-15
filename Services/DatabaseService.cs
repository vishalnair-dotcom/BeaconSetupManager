using Npgsql;

namespace BeaconSetupManager.Services;

public class DatabaseService : IDatabaseService
{
    public static void ValidateInputs(string host, string name, string user, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Enter a PostgreSQL host.");
        if (port is < 1 or > 65535) throw new ArgumentException("PostgreSQL port must be between 1 and 65535.");
        foreach (string identifier in new[] { name, user })
            if (string.IsNullOrWhiteSpace(identifier) || System.Text.Encoding.UTF8.GetByteCount(identifier) > 63 || identifier.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                throw new ArgumentException("Database and role names must be 1–63 UTF-8 bytes, using letters, digits, and underscores.");
    }
    private static string Connection(string host, string user, string password, string database, int port = 5432)
        => new NpgsqlConnectionStringBuilder { Host = host, Port = port, Username = user, Password = password, Database = database, Timeout = 10, CommandTimeout = 15 }.ConnectionString;
    public async Task<bool> TestAdminConnectionAsync(string host, string adminUser, string adminPassword)
    {
        try { await using var connection = new NpgsqlConnection(Connection(host, adminUser, adminPassword, "postgres")); await connection.OpenAsync(); return true; }
        catch { return false; }
    }
    public async Task<bool> SetupDatabaseAsync(string host, string adminUser, string adminPassword, string dbName, string dbUser, string dbPassword,
        Action<string>? onProgress = null, CancellationToken cancellationToken = default, int port = 5432, bool updateExistingRolePassword = false, bool updateExistingDatabaseOwner = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInputs(host, dbName, dbUser, port);
        if (string.IsNullOrEmpty(dbPassword)) throw new ArgumentException("Enter an application role password before creating or verifying its database.");
        if (string.IsNullOrWhiteSpace(adminUser)) throw new ArgumentException("Enter an administrator role.");
        try
        {
            await using var admin = new NpgsqlConnection(Connection(host, adminUser, adminPassword, "postgres", port));
            await admin.OpenAsync(cancellationToken);
            async Task<bool> Exists(string sql, string value)
            {
                await using var command = new NpgsqlCommand(sql, admin);
                command.Parameters.AddWithValue("value", value);
                return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
            }
            async Task Execute(string sql, NpgsqlConnection connection)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            var quote = new NpgsqlCommandBuilder();
            string role = quote.QuoteIdentifier(dbUser), database = quote.QuoteIdentifier(dbName);
            string delimiter;
            do { delimiter = "$beacon_" + Guid.NewGuid().ToString("N") + "$"; } while (dbPassword.Contains(delimiter));
            string password = delimiter + dbPassword + delimiter;
            bool roleExists = await Exists("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@value)", dbUser);
            if (!roleExists) await Execute($"CREATE ROLE {role} LOGIN PASSWORD {password}", admin);
            else if (updateExistingRolePassword) await Execute($"ALTER ROLE {role} LOGIN PASSWORD {password}", admin);
            else onProgress?.Invoke("Existing role credentials retained. Select Update Existing Role Password if a change is intended.");
            bool databaseExists = await Exists("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname=@value)", dbName);
            if (!databaseExists) await Execute($"CREATE DATABASE {database} OWNER {role}", admin);
            else if (updateExistingDatabaseOwner) await Execute($"ALTER DATABASE {database} OWNER TO {role}", admin);
            await using var target = new NpgsqlConnection(Connection(host, adminUser, adminPassword, dbName, port));
            await target.OpenAsync(cancellationToken);
            await Execute("CREATE EXTENSION IF NOT EXISTS citext", target);
            await Execute("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\"", target);
            onProgress?.Invoke("Database, role, and extensions verified. Schema upgrades run separately when the backend starts with Allow Database Upgrade.");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { onProgress?.Invoke("Database setup failed. Earlier completed changes are retained and may be reused on retry: " + ex.Message); return false; }
    }
    public async Task<(bool DbExists, bool RoleExists, bool ExtensionsInstalled)> ValidateDatabaseSetupAsync(string host, string adminUser, string adminPassword, string dbName, string dbUser)
    {
        ValidateInputs(host, dbName, dbUser, 5432);
        try
        {
            await using var admin = new NpgsqlConnection(Connection(host, adminUser, adminPassword, "postgres"));
            await admin.OpenAsync();
            await using var dbCommand = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname=@name)", admin);
            dbCommand.Parameters.AddWithValue("name", dbName);
            bool db = (bool)(await dbCommand.ExecuteScalarAsync() ?? false);
            await using var roleCommand = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@name)", admin);
            roleCommand.Parameters.AddWithValue("name", dbUser);
            bool role = (bool)(await roleCommand.ExecuteScalarAsync() ?? false);
            if (!db) return (false, role, false);
            await using var target = new NpgsqlConnection(Connection(host, adminUser, adminPassword, dbName));
            await target.OpenAsync();
            await using var extensionCommand = new NpgsqlCommand("SELECT COUNT(*) FROM pg_extension WHERE extname IN ('citext','uuid-ossp')", target);
            return (db, role, (long)(await extensionCommand.ExecuteScalarAsync() ?? 0L) == 2);
        }
        catch { return (false, false, false); }
    }
}
