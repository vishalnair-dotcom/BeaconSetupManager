using System.Data.Common;
using BeaconSetupManager.Models;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace BeaconSetupManager.Services;

public interface IDatabaseConnectionVerifier
{
    Task VerifyAsync(DatabaseConnectionSettings settings, CancellationToken token);
}

public sealed class DatabaseConnectionVerifier : IDatabaseConnectionVerifier
{
    public Task VerifyAsync(DatabaseConnectionSettings settings, CancellationToken token) => DatabaseConnections.VerifyAsync(settings, token);
}

public static class DatabaseConnections
{
    public static string Build(DatabaseConnectionSettings settings)
    {
        if (settings.Engine == DatabaseEngine.PostgreSql)
            return new NpgsqlConnectionStringBuilder { Host = settings.Server, Port = settings.Port, Database = settings.Database,
                Username = settings.Username, Password = settings.Password }.ConnectionString;
        if (settings.Engine != DatabaseEngine.SqlServer) throw new ArgumentException("Unsupported database type.");
        // Keep Beacon's supplied five-field format and spelling. The generic builder
        // safely quotes values without rewriting aliases or adding client defaults.
        return new DbConnectionStringBuilder
        {
            ["Data Source"] = settings.Server,
            ["Initial Catalog"] = settings.Database,
            ["User ID"] = settings.Username,
            ["Password"] = settings.Password,
            ["TrustServerCertificate"] = settings.TrustServerCertificate.ToString()
        }.ConnectionString;
    }

    public static DbConnection Create(DatabaseConnectionSettings settings) => settings.Engine switch
    {
        DatabaseEngine.PostgreSql => new NpgsqlConnection(new NpgsqlConnectionStringBuilder(settings.OriginalConnectionString ?? Build(settings)) { Timeout = 10, CommandTimeout = 15 }.ConnectionString),
        DatabaseEngine.SqlServer => new SqlConnection(new SqlConnectionStringBuilder(settings.OriginalConnectionString ?? Build(settings)) { ConnectTimeout = 10 }.ConnectionString),
        _ => throw new ArgumentException("Unsupported database type.")
    };

    public static DatabaseConnectionSettings Parse(string connectionString, string? provider)
    {
        if (string.Equals(provider, "Npgsql", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(provider) && new DbConnectionStringBuilder { ConnectionString = connectionString }.ContainsKey("Host"))
        {
            var pg = new NpgsqlConnectionStringBuilder(connectionString);
            return new() { Engine = DatabaseEngine.PostgreSql, Server = pg.Host ?? "localhost", Port = pg.Port,
                Database = pg.Database ?? "", Username = pg.Username ?? "", Password = pg.Password ?? "", OriginalConnectionString = connectionString };
        }
        if (!string.IsNullOrEmpty(provider) && !string.Equals(provider, "System.Data.SqlClient", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(provider, "Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The configured database provider is not supported: " + provider);
        var sql = new SqlConnectionStringBuilder(connectionString);
        if (sql.IntegratedSecurity || sql.Authentication != SqlAuthenticationMethod.NotSpecified && sql.Authentication != SqlAuthenticationMethod.SqlPassword)
            throw new ArgumentException("Only SQL Server username/password authentication is supported.");
        return new() { Engine = DatabaseEngine.SqlServer, Server = sql.DataSource, Database = sql.InitialCatalog,
            Username = sql.UserID, Password = sql.Password, TrustServerCertificate = sql.TrustServerCertificate, OriginalConnectionString = connectionString };
    }

    public static async Task VerifyAsync(DatabaseConnectionSettings settings, CancellationToken token)
    {
        await using var connection = Create(settings);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = 15;
        await command.ExecuteScalarAsync(token);
    }
}
