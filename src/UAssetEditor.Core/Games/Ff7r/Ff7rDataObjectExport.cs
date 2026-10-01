using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;

namespace UAssetEditor.Core.Games.Ff7r;

/// <summary>
/// An FF7R DataObject table, decoded into ordinary properties: <see cref="NormalExport.Data"/> holds one struct per
/// row (named with the row's tag) whose members are the row's fields, arrays as array properties. Property paths,
/// dump, set and search then work on it like on any other export.
/// The table's bytes are re-emitted from those properties on save, so only edits that keep every cell the size it had
/// are accepted; anything else makes the save fail with a message naming the row and field, before anything is written.
/// </summary>
public sealed class Ff7rDataObjectExport : NormalExport
{
    private Ff7rTableLayout? _layout;

    /// <summary>Needed by <see cref="Export.ConvertToChildExport{T}"/>; an export built this way carries no table until <see cref="Adopt"/> runs.</summary>
    public Ff7rDataObjectExport()
    {
    }

    internal void Adopt(Ff7rTableLayout layout, List<PropertyData> rows)
    {
        _layout = layout;
        Data = rows;
        ObjectGuid = null;

        // The decoded table covers the export's whole body, so there is no native tail left to write after it.
        Extras = [];
    }

    public override void Write(AssetBinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var layout = _layout ?? throw new InvalidOperationException("This DataObject table was never decoded, so it can't be written.");
        writer.Write(Ff7rTableCodec.Encode(layout, Data, writer.Asset));
    }
}
