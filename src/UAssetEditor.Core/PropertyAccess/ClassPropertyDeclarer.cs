using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Declares a whole new top-level struct property on a Blueprint-generated class, cloned from
/// an existing one of the same type - e.g. a brand new "AnimGraphNode_KawaiiPhysics_N" entry
/// on an Animation Blueprint. This is the one piece plain property editing can't reach: a new
/// class member is a new <see cref="FProperty"/> declaration on the class's own
/// <see cref="StructExport.LoadedProperties"/>, not a <see cref="PropertyData"/> array element
/// of anything that already exists.
///
/// Deliberately narrow: this only declares the property and clones its CDO default value -
/// both ordinary, low-risk array/list appends. It does NOT touch the class's AnimNodeData
/// bookkeeping array or any ComponentPose.LinkID pose-chain wiring - those are already
/// editable through the existing array-duplicate and grid-edit tools, and are left for the
/// caller to set by hand: getting the compiled node index wrong would silently produce a node
/// the anim graph never runs, which is safer caught by a person checking real numbers against
/// a real file than guessed at automatically here.
/// </summary>
public static class ClassPropertyDeclarer
{
    /// <summary>
    /// Clones the class-level struct property named <paramref name="templateName"/> - found by
    /// locating whichever class in this asset declares <paramref name="cdoExportIndex"/> as its
    /// ClassDefaultObject - under a new name that follows the template's own numbering
    /// convention (e.g. "AnimGraphNode_KawaiiPhysics_34" -> "..._35"). Returns the new name and
    /// the cloned CDO value, so the caller can locate it afterward the same way as any other
    /// top-level property.
    /// </summary>
    public static (string NewName, PropertyData Value) DeclareClonedProperty(UAsset asset, int cdoExportIndex, string templateName)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(templateName);

        if (cdoExportIndex < 0 || cdoExportIndex >= asset.Exports.Count || asset.Exports[cdoExportIndex] is not NormalExport cdo)
            throw new ArgumentException("Not a valid export.", nameof(cdoExportIndex));

        var classExport = FindOwningClass(asset, cdoExportIndex)
            ?? throw new InvalidOperationException("No class in this asset declares this export as its ClassDefaultObject.");

        if (classExport.LoadedProperties.FirstOrDefault(p => FNameDisplay.ToDisplayString(p.Name) == templateName) is not FStructProperty template)
            throw new InvalidOperationException($"'{templateName}' isn't a declared struct property on this class.");

        var templateValue = cdo.Data.FirstOrDefault(p => p.Name.Value?.Value == templateName)
            ?? throw new InvalidOperationException($"'{templateName}' has no default value on this export.");

        var (baseName, nextNumber) = NextAvailableName(templateName, classExport.LoadedProperties.Select(p => FNameDisplay.ToDisplayString(p.Name)));
        var newName = $"{baseName}_{nextNumber}";

        // A real compiled anim-graph class declares every "Foo_N" sibling as FName(String="Foo",
        // Number=N+1) - Unreal's own FName.ToString() convention (Number 0 = no suffix, Number K
        // >= 1 displays as "_{K-1}") - NOT as one literal "Foo_N" string with Number 0. The CDO's
        // own Data list uses the opposite convention (the suffix baked into the string, Number 0 -
        // confirmed against a real asset), which is why clonedValue.Name below stays as-is.
        var clonedProperty = new FStructProperty
        {
            Name = new FName(asset, baseName, nextNumber + 1),
            SerializedType = template.SerializedType,
            Flags = template.Flags,
            MetaDataMap = new TMap<FName, FString>(),
            ArrayDim = template.ArrayDim,
            ElementSize = template.ElementSize,
            PropertyFlags = template.PropertyFlags,
            RepIndex = template.RepIndex,
            RepNotifyFunc = template.RepNotifyFunc,
            BlueprintReplicationCondition = template.BlueprintReplicationCondition,
            Struct = template.Struct,
        };
        classExport.LoadedProperties = [.. classExport.LoadedProperties, clonedProperty];

        var clonedValue = (PropertyData)templateValue.Clone();
        clonedValue.Name = new FName(asset, newName);
        cdo.Data.Add(clonedValue);

        return (newName, clonedValue);
    }

    private static ClassExport? FindOwningClass(UAsset asset, int cdoExportIndex)
    {
        foreach (var export in asset.Exports)
        {
            if (export is ClassExport classExport && classExport.ClassDefaultObject.IsExport() && classExport.ClassDefaultObject.Index - 1 == cdoExportIndex)
                return classExport;
        }

        return null;
    }

    /// <summary>
    /// "AnimGraphNode_KawaiiPhysics_34" among ["..._1" .. "..._34", "AnimGraphNode_KawaiiPhysics"]
    /// -> ("AnimGraphNode_KawaiiPhysics", 35) - matches the numbering scheme these files already
    /// use, so a cloned node reads as one more of the same kind, not an oddly-named outlier.
    /// <paramref name="existingDisplayNames"/> must already be in display form (see
    /// <see cref="FNameDisplay.ToDisplayString"/>), not raw FName.Value strings -
    /// LoadedProperties entries don't carry their "_N" suffix in the string at all, only in
    /// FName's own Number field.
    /// </summary>
    private static (string BaseName, int NextNumber) NextAvailableName(string templateName, IEnumerable<string> existingDisplayNames)
    {
        var underscoreIndex = templateName.LastIndexOf('_');
        var baseName = underscoreIndex >= 0 && int.TryParse(templateName[(underscoreIndex + 1)..], out _)
            ? templateName[..underscoreIndex]
            : templateName;

        var existing = new HashSet<string>(existingDisplayNames, StringComparer.Ordinal);
        var next = 1;
        while (existing.Contains($"{baseName}_{next}"))
            next++;

        return (baseName, next);
    }
}
