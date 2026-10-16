using System.IO;
using System.Xml.Linq;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services;

public class ConfigService : IConfigService
{
    public async Task<bool> SetupConfigurationAsync(string repoRoot, string dbHost, string dbName, string dbUser, string dbPassword,
        bool isDatabaseSetup = false, Action<string>? onProgress = null, CancellationToken cancellationToken = default, int port = 5432,
        DatabaseEngine engine = DatabaseEngine.PostgreSql, bool trustServerCertificate = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(repoRoot, "SheetKraftWeb", "ConfigSamples", "Runner");
            var destination = Path.Combine(repoRoot, "Config");
            WorkspacePaths.EnsureContainedWithoutLinks(repoRoot, source);
            WorkspacePaths.EnsureContainedWithoutLinks(repoRoot, destination);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Configuration samples are missing: " + source);
            Directory.CreateDirectory(destination);
            foreach (string file in WorkspacePaths.EnumerateSafeFiles(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                WorkspacePaths.EnsureContainedWithoutLinks(repoRoot, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(file, target);
            }
            string loggingSource = Path.Combine(repoRoot, "SheetKraftWeb", "ConfigSamples", "SheetKraftWeb.log4net.xml");
            string loggingTarget = Path.Combine(destination, "SheetKraftWeb.log4net.xml");
            if (File.Exists(loggingSource) && !File.Exists(loggingTarget)) File.Copy(loggingSource, loggingTarget);
            string connections = Path.Combine(destination, "connections.config");
            WorkspacePaths.EnsureContainedWithoutLinks(repoRoot, connections);
            var document = File.Exists(connections) ? XDocument.Load(connections) : new XDocument(new XElement("connectionStrings"));
            var root = document.Root;
            if (root?.Name != "connectionStrings") throw new InvalidDataException("connections.config must have a connectionStrings root. The original file has been retained.");
            foreach (var comment in document.DescendantNodes().OfType<XComment>()
                .Where(comment => comment.Value.TrimStart().StartsWith("The ReportComparer connection is required", StringComparison.OrdinalIgnoreCase)).ToArray())
                comment.Remove();
            var entry = root.Elements("add").FirstOrDefault(element => string.Equals((string?)element.Attribute("name"), "SheetKraft", StringComparison.OrdinalIgnoreCase));
            if (entry == null) { entry = new XElement("add", new XAttribute("name", "SheetKraft")); root.Add(entry); }
            var settings = new DatabaseConnectionSettings { Engine = engine, Server = dbHost, Port = port, Database = dbName,
                Username = dbUser, Password = dbPassword, TrustServerCertificate = trustServerCertificate };
            entry.SetAttributeValue("connectionString", DatabaseConnections.Build(settings));
            entry.SetAttributeValue("providerName", engine == DatabaseEngine.PostgreSql ? "Npgsql" : null);
            cancellationToken.ThrowIfCancellationRequested();
            WorkspacePaths.EnsureContainedWithoutLinks(repoRoot, connections + ".bak");
            if (File.Exists(connections)) File.Copy(connections, connections + ".bak", true);
            string temporary = connections + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, document.ToString(), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, connections, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            onProgress?.Invoke("Updated the SheetKraft connection; other connection definitions were retained.");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { onProgress?.Invoke("Configuration setup failed. Existing files were retained where possible: " + ex.Message); return false; }
    }
    public static string ReadApplicationConnection(string root)
    {
        var document = XDocument.Load(Path.Combine(root, "Config", "connections.config"));
        return document.Root?.Elements("add").FirstOrDefault(element => string.Equals((string?)element.Attribute("name"), "SheetKraft", StringComparison.OrdinalIgnoreCase))?.Attribute("connectionString")?.Value
            ?? throw new InvalidDataException("SheetKraft connection is missing from connections.config.");
    }
    public static DatabaseConnectionSettings ReadApplicationSettings(string root)
    {
        var document = XDocument.Load(Path.Combine(root, "Config", "connections.config"));
        var entry = document.Root?.Elements("add").FirstOrDefault(element => string.Equals((string?)element.Attribute("name"), "SheetKraft", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("SheetKraft connection is missing from connections.config.");
        return DatabaseConnections.Parse((string?)entry.Attribute("connectionString") ?? "", (string?)entry.Attribute("providerName"));
    }
}
