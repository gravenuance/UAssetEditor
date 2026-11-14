using System.Text.Json;
using ModelContextProtocol;
using UAssetEditor.Core.Editing;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Reads the rule set argument of the ruleset tools and of repack.</summary>
internal static class RulesetInput
{
    public const int MaxCharacters = 262_144;

    public const string Description =
        "The rule set: either its JSON text (starting with '{') or the absolute path of a .json file. Same document as the app's " +
        "batch-edit tab and 'uacli batch --ruleset': {\"name\":\"...\",\"scope\":{\"exportNameTerms\":[{\"text\":\"Kawaii\"}],\"propertyNameTerms\":[...]," +
        "\"valueTerms\":[...],\"referenceTerms\":[...]},\"rules\":[{\"kind\":\"setValue\",\"newValue\":\"0.5\"}]}. Rule kinds: setValue, numericAdjust, " +
        "replaceText, removeProperty, addTag, removeTag, replaceReference, duplicateElement. Enums may be written by name. " +
        "A scope with no terms matches nothing.";

    public static RuleSet Load(string? ruleset)
    {
        var text = ToolInput.Required(ruleset, nameof(ruleset), MaxCharacters);
        var json = text.StartsWith('{') ? text : ReadFile(text);
        try
        {
            var parsed = JsonSerializer.Deserialize<RuleSet>(json, AssetIo.RuleSetJsonOptions)
                ?? throw new McpException("ruleset did not contain a rule set.");
            return parsed.Rules.Count > 0 ? parsed : throw new McpException("ruleset has no rules.");
        }
        catch (JsonException ex)
        {
            throw new McpException($"ruleset is not a valid rule set: {Untrusted.Clean(ex.Message, 300)}", ex);
        }
    }

    private static string ReadFile(string path)
    {
        var file = ToolPaths.ExistingFile(path, "ruleset", ".json");
        return new FileInfo(file).Length <= MaxCharacters
            ? File.ReadAllText(file)
            : throw new McpException($"ruleset file is limited to {MaxCharacters} bytes.");
    }
}
