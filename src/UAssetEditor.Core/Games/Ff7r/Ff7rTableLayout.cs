namespace UAssetEditor.Core.Games.Ff7r;

/// <summary>The column types a DataObject table can hold. The numbers are the ones the game writes.</summary>
internal enum Ff7rFieldType : byte
{
    Boolean = 1,
    Byte = 2,
    BooleanByte = 3,
    UInt16 = 4,
    Int32 = 7,
    Float = 9,
    String = 10,
    Name = 11,
}

/// <summary>One column: its name-map reference exactly as stored, its type, and whether it holds an array (the game's convention is a name ending in "_Array").</summary>
internal readonly record struct Ff7rField(int NameIndex, int NameNumber, string Name, Ff7rFieldType Type, bool IsArray);

/// <summary>What one cell looked like in the file: its encoded size, its array length, and whether a scalar string was UTF-16. Edits must leave all of these alone.</summary>
internal readonly record struct Ff7rCellShape(int Size, int Count, bool Utf16);

/// <summary>Everything about a table that its rows, as properties, don't carry: the header bytes, the columns, each row's tag and the original cell shapes.</summary>
internal sealed class Ff7rTableLayout
{
    public required byte[] Header { get; init; }
    public required Ff7rField[] Fields { get; init; }
    public required string[] RowTags { get; init; }
    public required Ff7rCellShape[] Cells { get; init; }
    public required int BodyLength { get; init; }

    /// <summary>The name map's size when the table was loaded; a name at or beyond it was added since, and the package header can't grow.</summary>
    public required int NameCountAtLoad { get; init; }

    public int RowCount => RowTags.Length;
}
