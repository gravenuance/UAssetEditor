using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace UAssetEditor.Core.Editing;

/// <summary>
/// One batch-edit operation. Property-scoped rules (everything except
/// <see cref="ReplaceReferenceRule"/>) are applied to every property matched by the
/// owning <see cref="RuleSet"/>'s <see cref="RuleSet.Scope"/>. <see cref="ReplaceReferenceRule"/>
/// instead walks the asset's import table directly, since references aren't properties.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SetPropertyValueRule), "setValue")]
[JsonDerivedType(typeof(NumericAdjustRule), "numericAdjust")]
[JsonDerivedType(typeof(ReplaceTextRule), "replaceText")]
[JsonDerivedType(typeof(RemovePropertyRule), "removeProperty")]
[JsonDerivedType(typeof(AddTagRule), "addTag")]
[JsonDerivedType(typeof(RemoveTagRule), "removeTag")]
[JsonDerivedType(typeof(ReplaceReferenceRule), "replaceReference")]
[JsonDerivedType(typeof(DuplicateElementRule), "duplicateElement")]
public abstract class EditRule
{
}

public enum SkipComparison
{
    /// <summary>Current value equals <see cref="SkipCondition.Value"/>.</summary>
    Eq,
    /// <summary>Numeric-only: current value is less than <see cref="SkipCondition.Value"/>.</summary>
    Lt,
    /// <summary>Numeric-only: current value is greater than <see cref="SkipCondition.Value"/>.</summary>
    Gt,
    /// <summary>Numeric-only: current value is less than or equal to <see cref="SkipCondition.Value"/>.</summary>
    Lte,
    /// <summary>Numeric-only: current value is greater than or equal to <see cref="SkipCondition.Value"/>.</summary>
    Gte,
}

/// <summary>
/// Leaves a matched property untouched when its current value satisfies this condition
/// against <see cref="Value"/>. Bool/string/name properties only support <see cref="SkipComparison.Eq"/>.
/// </summary>
public sealed class SkipCondition
{
    public required SkipComparison Comparison { get; init; }
    public required string Value { get; init; }
}

/// <summary>Overwrites a matched property's value, parsed from its string form (e.g. "true", "3.14", "NewName").</summary>
public sealed class SetPropertyValueRule : EditRule
{
    public required string NewValue { get; init; }
    public SkipCondition? Skip { get; init; }
}

/// <summary>Applies an arithmetic operation to a matched Int/Float property's current value.</summary>
public sealed class NumericAdjustRule : EditRule
{
    /// <summary>One of "set", "add", "sub", "mul", "div".</summary>
    public required string Operation { get; init; }
    public required string TargetValue { get; init; }
    public SkipCondition? Skip { get; init; }
}

/// <summary>Finds/replaces text within a matched string-like property's value.</summary>
public sealed class ReplaceTextRule : EditRule
{
    public required string Pattern { get; init; }
    public required string Replacement { get; init; }
    public bool IsRegex { get; init; }
}

/// <summary>
/// Removes a matched property outright. Only supported for properties that live directly
/// in an export's or struct's property list (not for individual array elements — use
/// <see cref="RemoveTagRule"/> to drop an element out of a name array instead).
/// </summary>
public sealed class RemovePropertyRule : EditRule
{
}

/// <summary>Appends a name to a matched array-of-Name property (e.g. a simple gameplay tag list).</summary>
public sealed class AddTagRule : EditRule
{
    public required string Tag { get; init; }
}

/// <summary>Removes any element equal to <see cref="Tag"/> from a matched array-of-Name property.</summary>
public sealed class RemoveTagRule : EditRule
{
    public required string Tag { get; init; }
}

/// <summary>
/// Repoints hard object references: any import whose dotted path matches
/// <see cref="OldReference"/> has its object name rewritten. This is scoped to the
/// asset's import table and ignores <see cref="RuleSet.Scope"/>.
/// </summary>
public sealed class ReplaceReferenceRule : EditRule
{
    public required string OldReference { get; init; }
    public required string NewReference { get; init; }
    public bool IsRegex { get; init; }
}

/// <summary>Sets one field of a just-duplicated element (see <see cref="DuplicateElementRule"/>), addressed by its path relative to the element itself (e.g. "RootBone", "PhysicsSettings.Damping").</summary>
public sealed class FieldOverride
{
    public required string Path { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// Appends a clone of a matched array's own last element - e.g. one more "Chains" entry,
/// copied from whichever chain the matched node already has - then applies <see cref="Overrides"/>
/// to just that new clone. The batch counterpart to the Browse tree's "Duplicate Element"
/// context-menu action (<see cref="PropertyAccess.ArrayElementEditor.Duplicate"/>): one rule
/// grows every array the scope matches and specializes each new element in the same pass,
/// instead of duplicating and hand-editing one node at a time. Always clones the template
/// from within the SAME array it's appending to (never a value from elsewhere), so the clone
/// is guaranteed to match that array's own element schema.
/// </summary>
public sealed class DuplicateElementRule : EditRule
{
    public Collection<FieldOverride> Overrides { get; init; } = new();
}
