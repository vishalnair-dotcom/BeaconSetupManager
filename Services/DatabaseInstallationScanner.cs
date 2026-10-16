using System.ServiceProcess;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services;

public sealed record LocalDatabaseService(string Name, string DisplayName, string State);

public static class DatabaseInstallationScanner
{
    public static PrerequisiteItem Scan(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var services = ServiceController.GetServices();
            try
            {
                var found = new List<LocalDatabaseService>();
                foreach (var service in services)
                {
                    token.ThrowIfCancellationRequested();
                    string name = service.ServiceName, display = service.DisplayName;
                    if (!IsPostgres(name, display) && !IsSqlServer(name)) continue;
                    string state;
                    try { state = service.Status.ToString(); }
                    catch (Exception) { state = "state unavailable"; }
                    found.Add(new(name, display, state));
                }
                return Describe(found);
            }
            finally { foreach (var service in services) service.Dispose(); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PrerequisiteItem { Name = "Database server", RequiredVersion = "PostgreSQL or SQL Server", IsOptional = true,
                Status = ValidationStatus.Failed, DetectedVersion = "Scan unavailable",
                Message = "Could not inspect local PostgreSQL / SQL Server services: " + ex.Message + " Remote connectivity is verified during database setup." };
        }
    }

    private static bool IsPostgres(string name, string display) => name.Equals("postgresql", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("postgresql-", StringComparison.OrdinalIgnoreCase) || display.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase);
    private static bool IsSqlServer(string name) => name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("MSSQL$", StringComparison.OrdinalIgnoreCase);

    internal static PrerequisiteItem Describe(IEnumerable<LocalDatabaseService> services)
    {
        var entries = services.ToArray();
        var postgres = entries.Where(s => IsPostgres(s.Name, s.DisplayName)).ToArray();
        var sql = entries.Where(s => IsSqlServer(s.Name)).ToArray();
        string Detail(string label, LocalDatabaseService[] matches) => matches.Length == 0
            ? label + ": not detected locally."
            : label + ": installed — " + string.Join(", ", matches.OrderBy(s => s.Name).Select(s => s.Name + " (" + s.State + ")")) + ".";
        bool any = postgres.Length > 0 || sql.Length > 0;
        return new PrerequisiteItem { Name = "Database server", RequiredVersion = "PostgreSQL or SQL Server", IsOptional = true,
            Status = any ? ValidationStatus.Installed : ValidationStatus.Missing,
            DetectedVersion = postgres.Length > 0 && sql.Length > 0 ? "Both installed" : postgres.Length > 0 ? "PostgreSQL installed" : sql.Length > 0 ? "SQL Server installed" : "Neither detected locally",
            Message = Detail("PostgreSQL", postgres) + "\n" + Detail("SQL Server", sql) + "\nLocal Windows services only; portable/LocalDB installs are not detected. Remote servers can be used and are verified during database setup." };
    }
}
