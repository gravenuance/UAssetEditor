using System.Text.Json;
using UAssetEditor.Core.Editing;

namespace UAssetEditor.Tooling;

/// <summary>Reads a rule set (the JSON the app's batch-edit tab saves) for every front end, with the same limits and messages.</summary>
internal static class RuleSets
{
    public const int MaxCharacters = 262_144;

    /// <summary>The rule set in <paramref name="json"/>; one with no rules is refused, since applying it would change nothing.</summary>
    public static RuleSet Parse(string json, string name)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxCharacters) throw new ArgException($"{name} is limited to {MaxCharacters} characters.");
        try
        {
            var parsed = JsonSerializer.Deserialize<RuleSet>(json, AssetIo.RuleSetJsonOptions)
                ?? throw new ArgException($"{name} did not contain a rule set.");
            return parsed.Rules.Count > 0 ? parsed : throw new ArgException($"{name} has no rules.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            throw new ArgException($"{name} is not a valid rule set: {ex.Message}", ex);
        }
    }

    /// <summary>The rule set in the file at <paramref name="path"/>.</summary>
    public static RuleSet Load(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        var file = new FileInfo(path);
        if (!file.Exists) throw new ArgException($"{name} not found: {path}");
        if (file.Length > MaxCharacters) throw new ArgException($"{name} file is limited to {MaxCharacters} bytes.");
        return Parse(File.ReadAllText(path), name);
    }
}
