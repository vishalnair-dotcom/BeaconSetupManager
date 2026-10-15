using System.Text;

namespace BeaconSetupManager.Services;

public static class CommandArguments
{
    // Windows CommandLineToArgvW quoting, including backslashes preceding a quote/end quote.
    public static string Quote(string value)
    {
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ArgumentException("Arguments cannot contain line breaks or NUL.");
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static string PowerShellLiteral(string value) => "'" + value.Replace("'", "''") + "'";
    public static string[] Split(string value)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, started = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\')
            {
                int count = 1;
                while (i + 1 < value.Length && value[i + 1] == '\\') { count++; i++; }
                if (i + 1 < value.Length && value[i + 1] == '"')
                {
                    current.Append('\\', count / 2); i++;
                    if (count % 2 == 1) current.Append('"'); else quoted = !quoted;
                }
                else current.Append('\\', count);
                started = true;
            }
            else if (c == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(c) && !quoted) { if (started) { arguments.Add(current.ToString()); current.Clear(); started = false; } }
            else { current.Append(c); started = true; }
        }
        if (quoted) throw new ArgumentException("Additional arguments contain an unmatched quote.");
        if (started) arguments.Add(current.ToString());
        return arguments.ToArray();
    }
    public static string EncodedPowerShell(string command) => "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    public static string ValidateReference(string value)
    {
        value = value.Trim();
        if (string.IsNullOrEmpty(value) || value.StartsWith('-') || value.Any(char.IsControl) || value.Contains('"'))
            throw new ArgumentException("Enter a branch, tag, or commit reference without command options or control characters.");
        return value;
    }
}
