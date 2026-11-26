using System.Text.Json;
using UAssetEditor.Core.Editing;

namespace UAssetEditor.Tooling;

/// <summary>Reads a rule set (the JSON the app's batch-edit tab saves) for every front end, with the same messages.</summary>
internal static class RuleSets
{
    /// <summary>The rule set in <paramref name="json"/>; one with no rules is refused, since applying it would change nothing.</summary>
    public static RuleSet Parse(string json, string name)
    {
        ArgumentNullException.ThrowIfNull(json);
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

    /// <summary>The rule set in the file at <paramref name="path"/>; a caller taking input it does not trust caps its size with <paramref name="maxBytes"/>.</summary>
    public static RuleSet Load(string path, string name, long? maxBytes = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var file = new FileInfo(path);
        if (!file.Exists) throw new ArgException($"{name} not found: {path}");
        if (file.Length > maxBytes) throw new ArgException($"{name} file is limited to {maxBytes} bytes.");
        return Parse(File.ReadAllText(path), name);
    }
}
