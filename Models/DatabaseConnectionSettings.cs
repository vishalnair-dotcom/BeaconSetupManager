namespace BeaconSetupManager.Models;

public enum DatabaseEngine { PostgreSql, SqlServer }

public sealed record DatabaseConnectionSettings
{
    public DatabaseEngine Engine { get; init; }
    public string Server { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Database { get; init; } = "Beacon";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public bool TrustServerCertificate { get; init; }
    // Used only for verifying an existing configuration without dropping its connection options.
    public string? OriginalConnectionString { get; init; }
    public override string ToString() => $"{Engine}: {Server}/{Database}";
}
