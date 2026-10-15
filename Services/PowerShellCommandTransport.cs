using System.IO;
using System.Text;

namespace BeaconSetupManager.Services;

internal static class PowerShellCommandTransport
{
    // CreateProcessWithTokenW has a much smaller limit than CreateProcessW. Use the same
    // short transport in both modes so its execution and exit behavior can be tested unelevated.
    internal const int CommandLineBudget = 1000;
    public static (string Arguments, Dictionary<string, string>? Environment) Prepare(
        string fileName, string arguments, Dictionary<string, string>? environment)
    {
        string executable = Path.GetFileNameWithoutExtension(fileName);
        if (fileName.Length + arguments.Length + 4 < CommandLineBudget ||
            !(executable.Equals("powershell", StringComparison.OrdinalIgnoreCase) || executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase)))
            return (arguments, environment);
        var parts = CommandArguments.Split(arguments);
        int encoded = Array.FindIndex(parts, argument => argument.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase));
        if (encoded < 0 || encoded + 1 >= parts.Length) return (arguments, environment);
        // Validate before transport; malformed input must not become an opaque child-start failure.
        Convert.FromBase64String(parts[encoded + 1]);
        string key = "BEACON_PS_PAYLOAD_" + Guid.NewGuid().ToString("N");
        var variables = environment == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        variables[key] = parts[encoded + 1];
        string bootstrap = "$s=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($env:" + key + ")); Remove-Item Env:" + key + "; & ([scriptblock]::Create($s))";
        parts[encoded + 1] = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
        string shortArguments = string.Join(" ", parts.Select(CommandArguments.Quote));
        if (fileName.Length + shortArguments.Length + 4 >= CommandLineBudget)
            throw new ArgumentException("The PowerShell executable path or remaining options exceed the Windows user-launch command limit.");
        return (shortArguments, variables);
    }
}
