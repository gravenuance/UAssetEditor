using UAssetEditor.Core.Editing;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Reads the rule set argument of the ruleset tools and of repack.</summary>
internal static class RulesetInput
{
    public const int MaxCharacters = RuleSets.MaxCharacters;

    public const string Description =
        "The rule set: either its JSON text (starting with '{') or the absolute path of a .json file. Same document as the app's " +
        "batch-edit tab and 'uacli batch --ruleset': {\"name\":\"...\",\"scope\":{\"exportNameTerms\":[{\"text\":\"Kawaii\"}],\"propertyNameTerms\":[...]," +
        "\"valueTerms\":[...],\"referenceTerms\":[...]},\"rules\":[{\"kind\":\"setValue\",\"newValue\":\"0.5\"}]}. Rule kinds: setValue, numericAdjust, " +
        "replaceText, removeProperty, addTag, removeTag, replaceReference, duplicateElement. Enums may be written by name. " +
        "A scope with no terms matches nothing.";

    public static RuleSet Load(string? ruleset)
    {
        var text = ToolInput.Required(ruleset, nameof(ruleset), MaxCharacters);
        return text.StartsWith('{')
            ? RuleSets.Parse(text, nameof(ruleset))
            : RuleSets.Load(ToolPaths.ExistingFile(text, nameof(ruleset), ".json"), nameof(ruleset));
    }
}
