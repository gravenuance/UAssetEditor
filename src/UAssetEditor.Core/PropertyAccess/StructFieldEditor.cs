using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Adds a field to a struct property in place. Unversioned/tagged serialization omits a
/// struct field entirely when it sits at its native default (e.g. a rigid body's
/// GravityScale/LinearDamping left unset on a Kinematic body) - so a field genuinely missing
/// from <see cref="StructPropertyData.Value"/> can't be found by <see cref="PropertyLocator.Locate"/>
/// for <c>set</c> to modify. This is the struct counterpart to
/// <see cref="ArrayElementEditor.AppendClone"/>: clone a same-shaped sibling field from
/// elsewhere (e.g. another body's own populated DefaultInstance) into the struct that's
/// missing it, giving <c>set</c> something to find afterward.
/// </summary>
public static class StructFieldEditor
{
    /// <summary>Deep-clones <paramref name="template"/> and appends it to <paramref name="target"/>'s own field list.</summary>
    public static PropertyData AppendClone(StructPropertyData target, PropertyData template)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(template);

        var clone = (PropertyData)template.Clone();
        target.Value.Add(clone);
        return clone;
    }

    /// <summary>
    /// Deep-clones <paramref name="template"/> and appends it to an export's own top-level
    /// property list (see <see cref="PropertyLocator.LocateExportData"/>) - for when the missing
    /// property is a whole top-level field (e.g. a rigid body's entire <c>DefaultInstance</c>
    /// struct, omitted because every field inside it sat at its engine default), not just a
    /// field within an already-present struct.
    /// </summary>
    public static PropertyData AppendClone(IList<PropertyData> target, PropertyData template)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(template);

        var clone = (PropertyData)template.Clone();
        target.Add(clone);
        return clone;
    }
}
