using System.Collections;
using System.Reflection;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Copies property values from one package into another. An FName is an index into its own
/// package's name table and an FPackageIndex points into its own import/export tables, so a
/// plain clone would write the donor's indices into the target. Every name is re-created in the
/// target, imports are found or copied by path, and donor exports map through <see cref="MapExport"/>.
/// </summary>
public sealed class CrossAssetCopier
{
    private readonly UAsset _donor;
    private readonly UAsset _target;
    private readonly Dictionary<int, FPackageIndex> _exportMap = [];

    public CrossAssetCopier(UAsset donor, UAsset target)
    {
        ArgumentNullException.ThrowIfNull(donor);
        ArgumentNullException.ThrowIfNull(target);
        _donor = donor;
        _target = target;
    }

    /// <summary>References to this donor export become references to <paramref name="targetExportIndex"/> (0-based).</summary>
    public void MapExport(int donorExportIndex, int targetExportIndex) =>
        _exportMap[donorExportIndex] = FPackageIndex.FromExport(targetExportIndex);

    public PropertyData Copy(PropertyData donorValue)
    {
        ArgumentNullException.ThrowIfNull(donorValue);
        var copy = (PropertyData)donorValue.Clone();
        Rehome(copy, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return copy;
    }

    public FName MapName(FName? name)
    {
        if (name == null) return null!;
        if (name.IsDummy) return name;
        return new FName(_target, name.Value.Value, name.Number);
    }

    public FPackageIndex MapIndex(FPackageIndex? index)
    {
        if (index == null || index.IsNull()) return index ?? new FPackageIndex(0);
        if (index.IsExport())
        {
            return _exportMap.TryGetValue(index.Index - 1, out var mapped)
                ? mapped
                : throw new InvalidOperationException($"The copied data references donor export '{_donor.Exports[index.Index - 1].ObjectName}', which has no counterpart in the target.");
        }
        return FPackageIndex.FromImport(EnsureImport(index.ToImport(_donor)));
    }

    /// <summary>The target's index for this donor import, adding it (and its outer chain) when missing.</summary>
    public int EnsureImport(Import donorImport)
    {
        ArgumentNullException.ThrowIfNull(donorImport);
        var outer = donorImport.OuterIndex.IsNull() ? new FPackageIndex(0) : MapIndex(donorImport.OuterIndex);
        var name = donorImport.ObjectName.Value.Value;

        for (var i = 0; i < _target.Imports.Count; i++)
        {
            var existing = _target.Imports[i];
            if (existing.ObjectName.Value.Value == name && existing.ObjectName.Number == donorImport.ObjectName.Number && existing.OuterIndex.Index == outer.Index)
                return i;
        }

        _target.Imports.Add(new Import(MapName(donorImport.ClassPackage), MapName(donorImport.ClassName), outer, MapName(donorImport.ObjectName), donorImport.bImportOptional)
        {
            PackageName = donorImport.PackageName == null ? null! : MapName(donorImport.PackageName),
        });
        return _target.Imports.Count - 1;
    }

    // Reflection keeps this correct for every property type UAssetAPI has, instead of a hand-kept list that silently misses one.
    private void Rehome(object node, HashSet<object> visited)
    {
        if (!visited.Add(node)) return;

        for (var type = node.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsInitOnly && field.FieldType.IsValueType) continue;
                var value = field.GetValue(node);
                var replaced = RehomeValue(value, visited);
                if (!ReferenceEquals(replaced, value) || field.FieldType.IsValueType) field.SetValue(node, replaced);
            }
        }
    }

    private object? RehomeValue(object? value, HashSet<object> visited)
    {
        switch (value)
        {
            case null or string or UAsset or INameMap or Type or Delegate:
                return value;
            case Array array when array.GetType().GetElementType()!.IsPrimitive:
                return value;
            case FName name:
                return MapName(name);
            case FPackageIndex index:
                return MapIndex(index);
            case IDictionary dictionary:
                RehomeDictionary(dictionary, visited);
                return value;
            case IList list:
                for (var i = 0; i < list.Count; i++)
                {
                    var element = list[i];
                    var replaced = RehomeValue(element, visited);
                    if (!ReferenceEquals(replaced, element) || (element != null && element.GetType().IsValueType)) list[i] = replaced;
                }
                return value;
        }

        var valueType = value.GetType();
        if (valueType.IsPrimitive || valueType.IsEnum || valueType.Assembly != typeof(UAsset).Assembly) return value;

        // Structs are copies: rehome the boxed copy and hand it back to be stored.
        if (valueType.IsValueType)
        {
            Rehome(value, new HashSet<object>(ReferenceEqualityComparer.Instance));
            return value;
        }

        Rehome(value, visited);
        return value;
    }

    private void RehomeDictionary(IDictionary dictionary, HashSet<object> visited)
    {
        var entries = new List<(object Key, object? Value)>();
        foreach (DictionaryEntry entry in dictionary) entries.Add((entry.Key, entry.Value));
        dictionary.Clear();
        foreach (var (key, entryValue) in entries)
            dictionary[RehomeValue(key, visited)!] = RehomeValue(entryValue, visited);
    }
}
