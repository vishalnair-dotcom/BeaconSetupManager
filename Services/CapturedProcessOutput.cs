using System.Text;

namespace BeaconSetupManager.Services;

// Streaming callers can retain a bounded tail without truncating machine-readable Git responses.
internal sealed class CapturedProcessOutput
{
    private readonly StringBuilder _text = new();
    private readonly int? _maximumCharacters;

    public CapturedProcessOutput(int? maximumCharacters)
    {
        if (maximumCharacters is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        _maximumCharacters = maximumCharacters;
    }

    public void AppendLine(string line)
    {
        lock (_text)
        {
            if (_maximumCharacters is int maximum && line.Length >= maximum)
            {
                _text.Clear();
                _text.Append(line.AsSpan(line.Length - maximum));
            }
            else
            {
                _text.Append(line);
            }
            _text.AppendLine();
            if (_maximumCharacters is int limit && _text.Length > limit)
                _text.Remove(0, _text.Length - limit);
        }
    }

    public override string ToString()
    {
        lock (_text) return _text.ToString();
    }
}
