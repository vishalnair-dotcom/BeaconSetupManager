using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BeaconSetupManager.Services;

public static class WorkspacePaths
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
    public static bool HasRepository(string path) => Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
    public static string Key(string path) => Normalize(path).ToUpperInvariant();
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Key(path))));
    public static string NormalizeRemote(string url)
    {
        var value = url.Trim().TrimEnd('/');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            value = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped).TrimEnd('/');
        return value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }
    public static bool SameRemote(string left, string right) => string.Equals(NormalizeRemote(left), NormalizeRemote(right), StringComparison.OrdinalIgnoreCase);
    public static void ValidateDeletion(string path)
    {
        var full = Normalize(path);
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase) || !HasRepository(full))
            throw new InvalidOperationException("Only an existing repository folder can be removed. Drive roots cannot be removed.");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Repository deletion through a directory link is not supported.");
    }
    public static void EnsureContainedWithoutLinks(string root, string target)
    {
        root = Normalize(root); target = Normalize(target);
        string relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException("The file must remain within the selected workspace.");
        string current = root;
        foreach (var part in new[] { "." }.Concat(relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("File operations through workspace links are not supported: " + current);
        }
    }
    public static IEnumerable<string> EnumerateSafeFiles(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Configuration links must be removed before copying files.");
            if ((attributes & FileAttributes.Directory) != 0)
            { foreach (string file in EnumerateSafeFiles(entry)) yield return file; }
            else yield return entry;
        }
    }
}
