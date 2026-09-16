namespace UAssetEditor.Cli;

/// <summary>Thrown for a malformed or missing command-line argument; caught at the top level and reported as a one-line error instead of a stack trace.</summary>
internal sealed class ArgException : Exception
{
    public ArgException() { }

    public ArgException(string message) : base(message) { }

    public ArgException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Splits a verb's remaining arguments into positionals and "--name value" / "--flag"
/// options - minimal on purpose, since every command here takes at most a couple of each.
/// </summary>
internal sealed class ArgReader
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public ArgReader(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                _positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            var hasValue = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal);
            if (hasValue)
            {
                _options[name] = list[++i];
            }
            else
            {
                _flags.Add(name);
            }
        }
    }

    public string Positional(int index, string label) =>
        index < _positional.Count ? _positional[index] : throw new ArgException($"Missing argument: {label}");

    public string? Positional(int index) => index < _positional.Count ? _positional[index] : null;

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public string RequireOption(string name) => Option(name) ?? throw new ArgException($"Missing --{name}");

    public bool Flag(string name) => _flags.Contains(name);
}
