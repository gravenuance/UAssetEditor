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
    // Every --name any command reads. Checked up front so a typo fails before anything runs, not after.
    private static readonly HashSet<string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "aes", "apply", "backup", "compression", "depth", "export", "export-name", "filter", "from", "from-export", "from-file", "nodes",
        "into", "into-export", "into-path", "jobs", "layer", "members", "mount", "no-paks-resolve", "ops", "pak-version", "path", "plan",
        "property-name", "reference", "regex", "ruleset", "save", "strict", "template", "usmap", "value", "version",
    };

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
            if (!KnownNames.Contains(name))
                throw new ArgException($"Unknown option --{name}.{Suggestion(name)}");

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

    /// <summary>These arguments plus how the run opens assets (--version, --usmap, --strict), unless already given.</summary>
    public ArgReader WithOpenSettingsFrom(ArgReader run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var merged = new ArgReader([]);
        merged._positional.AddRange(_positional);
        foreach (var (key, value) in _options) merged._options[key] = value;
        merged._flags.UnionWith(_flags);
        foreach (var name in (string[])["version", "usmap"])
        {
            if (!merged._options.ContainsKey(name) && run.Option(name) is { } value) merged._options[name] = value;
        }
        if (run.Flag("strict")) merged._flags.Add("strict");
        return merged;
    }

    private static string Suggestion(string name)
    {
        var closest = KnownNames.MinBy(known => EditDistance(name, known))!;
        return EditDistance(name, closest) <= Math.Max(2, name.Length / 3) || name.StartsWith(closest, StringComparison.OrdinalIgnoreCase)
            ? $" Did you mean --{closest}?"
            : "";
    }

    private static int EditDistance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }
            previous = current;
        }
        return previous[b.Length];
    }
}
