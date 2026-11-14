namespace UAssetEditor.Mcp.Tools;

/// <summary>Collects the output lines of a helper process for the tool result: each line cleaned, only the first
/// <c>maxLines</c> kept, the rest counted.</summary>
internal sealed class LogLines(int maxLines)
{
    private readonly List<string> _lines = [];
    private readonly Lock _lock = new();
    private int _dropped;

    public void Add(string line)
    {
        lock (_lock)
        {
            if (_lines.Count >= maxLines) _dropped++;
            else _lines.Add(Untrusted.Clean(line, 300));
        }
    }

    public IReadOnlyList<string> ToList()
    {
        lock (_lock)
        {
            return _dropped == 0 ? [.. _lines] : [.. _lines, $"... {_dropped} more line(s) not shown"];
        }
    }
}
