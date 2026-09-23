using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// A class's sparse data, which a cooked class default object carries after its own properties:
/// the index of the struct export describing it, then that struct's value. UAssetAPI keeps these
/// bytes opaque, so this reads them as a property tree and writes an edited tree back.
/// </summary>
internal sealed class SparseClassData
{
    private readonly UAsset _asset;
    private readonly NormalExport _cdo;
    private readonly int _structIndex;
    private readonly byte[] _trailing;

    private SparseClassData(UAsset asset, NormalExport cdo, int structIndex, StructPropertyData value, byte[] trailing)
    {
        _asset = asset;
        _cdo = cdo;
        _structIndex = structIndex;
        Value = value;
        _trailing = trailing;
    }

    public StructPropertyData Value { get; }

    /// <summary>The CDO's sparse data, or null when it carries none.</summary>
    public static SparseClassData? Read(UAsset asset, NormalExport cdo)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(cdo);
        if (cdo.Extras is not { Length: > sizeof(int) } extras) return null;

        using var stream = new MemoryStream(extras, writable: false);
        using var reader = new AssetBinaryReader(stream, asset);
        var structIndex = reader.ReadInt32();
        var index = FPackageIndex.FromRawIndex(structIndex);
        if (!index.IsExport() || index.ToExport(asset) is not StructExport structExport) return null;

        var value = new StructPropertyData(new FName(asset, "SparseClassData"), new FName(asset, structExport.ObjectName.ToString()));
        value.Read(reader, false, extras.Length - sizeof(int));
        return new SparseClassData(asset, cdo, structIndex, value, extras[(int)stream.Position..]);
    }

    /// <summary>The named member of <see cref="Value"/>, looked up through nested structs, e.g. "AnimBlueprintExtension_Base.ExposedValueHandlers".</summary>
    public T? Find<T>(string path) where T : PropertyData
    {
        ArgumentNullException.ThrowIfNull(path);
        PropertyData? current = Value;
        foreach (var part in path.Split('.'))
            current = (current as StructPropertyData)?.Value?.FirstOrDefault(p => FNameDisplay.ToDisplayString(p.Name) == part);
        return current as T;
    }

    /// <summary>Serializes <see cref="Value"/> back into the CDO's trailing bytes.</summary>
    public void Save()
    {
        using var stream = new MemoryStream();
        using (var writer = new AssetBinaryWriter(stream, _asset))
        {
            writer.Write(_structIndex);
            Value.Write(writer, false);
            writer.Write(_trailing);
        }
        _cdo.Extras = stream.ToArray();
    }
}
