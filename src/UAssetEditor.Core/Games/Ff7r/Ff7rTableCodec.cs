using System.Buffers.Binary;
using System.Text;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Games.Ff7r;

/// <summary>
/// Reads and writes the body of an FF7R DataObject export: a 10-byte header, the row and field counts, the field
/// list (name, type byte), then each row as its tag name followed by one value per field in field order. A field
/// whose name ends in "_Array" stores an int32 count and that many elements instead of one value.
/// Every length read from the file is checked against what is left, so a wrong guess about a file never reads past it.
/// </summary>
internal static class Ff7rTableCodec
{
    public const int HeaderLength = 10;
    public const string ArraySuffix = "_Array";

    private const int RowTagSize = 2 * sizeof(int);
    private const int FieldEntrySize = (2 * sizeof(int)) + 1;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);
    private static readonly UTF8Encoding LenientUtf8 = new(false, false);
    private static readonly UnicodeEncoding LenientUtf16 = new(false, false, false);

    /// <summary>
    /// Decodes <paramref name="body"/> into one struct per row. Returns null, with the reason in <paramref name="failure"/>,
    /// unless the body is well formed and encoding the result again reproduces it byte for byte.
    /// </summary>
    public static (Ff7rTableLayout Layout, List<PropertyData> Rows)? TryParse(byte[] body, UAsset asset, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(asset);
        try
        {
            var parsed = Parse(body, asset);
            var again = Encode(parsed.Layout, parsed.Rows, asset);
            if (!again.AsSpan().SequenceEqual(body))
            {
                failure = "encoding the decoded table again does not reproduce the original bytes";
                return null;
            }

            failure = null;
            return parsed;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or OverflowException or NameMapOutOfRangeException)
        {
            failure = ex.Message;
            return null;
        }
    }

    private static (Ff7rTableLayout Layout, List<PropertyData> Rows) Parse(byte[] body, UAsset asset)
    {
        var nameCount = asset.GetNameMapIndexList().Count;
        var cursor = new Cursor(body);
        var header = cursor.Bytes(HeaderLength);
        var rowCount = cursor.Int32();
        var fieldCount = cursor.Int32();
        if (rowCount < 0 || (long)rowCount * RowTagSize > cursor.Remaining)
            throw new InvalidDataException($"row count {rowCount} does not fit in the {cursor.Remaining} bytes that follow");
        if (fieldCount < 0 || (long)fieldCount * FieldEntrySize > cursor.Remaining)
            throw new InvalidDataException($"field count {fieldCount} does not fit in the {cursor.Remaining} bytes that follow");

        var fields = new Ff7rField[fieldCount];
        for (var f = 0; f < fieldCount; f++)
        {
            var (index, number) = cursor.NameReference(nameCount);
            var typeByte = cursor.Byte();
            if (!Enum.IsDefined((Ff7rFieldType)typeByte))
                throw new InvalidDataException($"field {f} has unknown type {typeByte}");
            var name = asset.GetNameReference(index)?.Value ?? throw new InvalidDataException($"field {f} has a null name");
            fields[f] = new Ff7rField(index, number, name, (Ff7rFieldType)typeByte, name.EndsWith(ArraySuffix, StringComparison.Ordinal));
        }

        // Bounds the cell table before it is allocated: every row needs its tag and the smallest value of each field.
        var minRowSize = RowTagSize + fields.Sum(f => (long)MinCellSize(f));
        if (rowCount * minRowSize > cursor.Remaining)
            throw new InvalidDataException($"{rowCount} rows of at least {minRowSize} bytes do not fit in the {cursor.Remaining} bytes that follow");

        var rowType = FName.DefineDummy(asset, "Ff7rDataObjectRow");
        var rows = new List<PropertyData>(rowCount);
        var tags = new string[rowCount];
        var cells = new Ff7rCellShape[checked(rowCount * fieldCount)];
        for (var r = 0; r < rowCount; r++)
        {
            var (tagIndex, tagNumber) = cursor.NameReference(nameCount);
            tags[r] = asset.GetNameReference(tagIndex)?.Value ?? throw new InvalidDataException($"row {r} has a null tag");
            var row = new StructPropertyData(new FName(asset, tagIndex, tagNumber)) { StructType = rowType };
            row.Value.Capacity = fieldCount;
            for (var f = 0; f < fieldCount; f++)
            {
                var start = cursor.Position;
                var field = fields[f];
                var name = new FName(asset, field.NameIndex, field.NameNumber);
                var utf16 = false;
                var count = 0;
                PropertyData value;
                if (field.IsArray)
                {
                    count = cursor.Int32();
                    if (count < 0 || (long)count * MinElementSize(field.Type) > cursor.Remaining)
                        throw new InvalidDataException($"row {r} field '{field.Name}' array length {count} does not fit in the {cursor.Remaining} bytes that follow");
                    var elements = new PropertyData[count];
                    for (var e = 0; e < count; e++)
                        elements[e] = ReadScalar(cursor, field.Type, name, inArray: true, nameCount, out _);
                    value = new ArrayPropertyData(name) { ArrayType = FName.DefineDummy(asset, PropertyTypeOf(field.Type)), Value = elements };
                }
                else
                {
                    value = ReadScalar(cursor, field.Type, name, inArray: false, nameCount, out utf16);
                }

                cells[(r * fieldCount) + f] = new Ff7rCellShape(cursor.Position - start, count, utf16);
                row.Value.Add(value);
            }

            rows.Add(row);
        }

        if (cursor.Remaining != 0)
            throw new InvalidDataException($"{cursor.Remaining} bytes are left over after the last row");

        var layout = new Ff7rTableLayout
        {
            Header = header,
            Fields = fields,
            RowTags = tags,
            Cells = cells,
            BodyLength = body.Length,
            NameCountAtLoad = nameCount,
        };
        return (layout, rows);
    }

    /// <summary>A cell's smallest encoding: an array's element count, or the scalar itself (a bool outside an array is an int32).</summary>
    private static int MinCellSize(Ff7rField field) =>
        field.IsArray || field.Type == Ff7rFieldType.Boolean ? sizeof(int) : MinElementSize(field.Type);

    private static int MinElementSize(Ff7rFieldType type) => type switch
    {
        Ff7rFieldType.Boolean or Ff7rFieldType.Byte or Ff7rFieldType.BooleanByte => 1,
        Ff7rFieldType.UInt16 => sizeof(ushort),
        Ff7rFieldType.Int32 or Ff7rFieldType.Float or Ff7rFieldType.String => sizeof(int),
        Ff7rFieldType.Name => 2 * sizeof(int),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown field type."),
    };

    private static string PropertyTypeOf(Ff7rFieldType type) => type switch
    {
        Ff7rFieldType.Boolean or Ff7rFieldType.BooleanByte => "BoolProperty",
        Ff7rFieldType.Byte => "ByteProperty",
        Ff7rFieldType.UInt16 => "UInt16Property",
        Ff7rFieldType.Int32 => "IntProperty",
        Ff7rFieldType.Float => "FloatProperty",
        Ff7rFieldType.String => "StrProperty",
        Ff7rFieldType.Name => "NameProperty",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown field type."),
    };

    private static PropertyData ReadScalar(Cursor cursor, Ff7rFieldType type, FName name, bool inArray, int nameCount, out bool utf16)
    {
        utf16 = false;
        switch (type)
        {
            case Ff7rFieldType.Boolean:
                // Stored as a whole int32 on its own, but packed to one byte inside an array.
                return new BoolPropertyData(name) { Value = ToBool(inArray ? cursor.Byte() : cursor.Int32()) };
            case Ff7rFieldType.BooleanByte:
                return new BoolPropertyData(name) { Value = ToBool(cursor.Byte()) };
            case Ff7rFieldType.Byte:
                return new BytePropertyData(name) { ByteType = BytePropertyType.Byte, Value = cursor.Byte() };
            case Ff7rFieldType.UInt16:
                return new UInt16PropertyData(name) { Value = cursor.UInt16() };
            case Ff7rFieldType.Int32:
                return new IntPropertyData(name) { Value = cursor.Int32() };
            case Ff7rFieldType.Float:
                return new FloatPropertyData(name) { Value = BitConverter.Int32BitsToSingle(cursor.Int32()) };
            case Ff7rFieldType.String:
                return new StrPropertyData(name) { Value = cursor.String(out utf16) };
            case Ff7rFieldType.Name:
                var (index, number) = cursor.NameReference(nameCount);
                return new NamePropertyData(name) { Value = new FName(name.Asset, index, number) };
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown field type.");
        }
    }

    private static bool ToBool(int value) => value switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException($"boolean value {value} is neither 0 nor 1"),
    };

    /// <summary>
    /// Encodes <paramref name="rows"/> back into a table body. Only edits that keep every cell the size it had in the
    /// file are accepted; anything else throws <see cref="InvalidOperationException"/> naming the row and field.
    /// </summary>
    public static byte[] Encode(Ff7rTableLayout layout, IReadOnlyList<PropertyData> rows, UAsset asset)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(asset);
        if (rows.Count != layout.RowCount) throw RowCountChanged(layout, rows);

        using var stream = new MemoryStream(layout.BodyLength);
        using var writer = new AssetBinaryWriter(stream, Encoding.UTF8, leaveOpen: true, asset);
        writer.Write(layout.Header);
        writer.Write(layout.RowCount);
        writer.Write(layout.Fields.Length);
        foreach (var field in layout.Fields)
        {
            writer.Write(field.NameIndex);
            writer.Write(field.NameNumber);
            writer.Write((byte)field.Type);
        }

        var fieldCount = layout.Fields.Length;
        for (var r = 0; r < rows.Count; r++)
        {
            var tag = layout.RowTags[r];
            if (rows[r] is not StructPropertyData row) throw new InvalidOperationException($"Row '{tag}' is no longer a table row.");
            RequireKnownName(asset, layout, row.Name, tag, "(row tag)");
            writer.Write(row.Name);
            if (row.Value.Count != fieldCount)
                throw new InvalidOperationException($"Row '{tag}' has {row.Value.Count} fields but the table has {fieldCount}; adding or removing fields is not supported.");

            for (var f = 0; f < fieldCount; f++)
            {
                var field = layout.Fields[f];
                var shape = layout.Cells[(r * fieldCount) + f];
                writer.Flush();
                var start = stream.Position;
                if (field.IsArray)
                {
                    if (row.Value[f] is not ArrayPropertyData array) throw WrongType(tag, field);
                    if (array.Value.Length != shape.Count)
                        throw new InvalidOperationException($"Row '{tag}' field '{field.Name}': array length changed from {shape.Count} to {array.Value.Length}; only same-size edits can be saved.");
                    writer.Write(array.Value.Length);
                    foreach (var element in array.Value)
                        WriteScalar(writer, asset, layout, element, field, tag, inArray: true, shape.Utf16);
                }
                else
                {
                    WriteScalar(writer, asset, layout, row.Value[f], field, tag, inArray: false, shape.Utf16);
                }

                writer.Flush();
                var size = (int)(stream.Position - start);
                if (size != shape.Size)
                    throw new InvalidOperationException($"Row '{tag}' field '{field.Name}': encoded size changed from {shape.Size} to {size} bytes (string length change?); only same-size edits can be saved.");
            }
        }

        writer.Flush();
        if (stream.Length != layout.BodyLength)
            throw new InvalidOperationException($"Table encodes to {stream.Length} bytes but was {layout.BodyLength} when loaded; only same-size edits can be saved.");
        return stream.ToArray();
    }

    private static InvalidOperationException RowCountChanged(Ff7rTableLayout layout, IReadOnlyList<PropertyData> rows)
    {
        if (rows.Count > layout.RowCount)
            return new InvalidOperationException($"Row '{FNameDisplay.ToDisplayString(rows[layout.RowCount].Name)}' was added; adding or removing rows is not supported.");
        for (var i = 0; i < rows.Count; i++)
        {
            if (!string.Equals(rows[i].Name?.Value?.Value, layout.RowTags[i], StringComparison.Ordinal))
                return new InvalidOperationException($"Row '{layout.RowTags[i]}' was removed; adding or removing rows is not supported.");
        }

        return new InvalidOperationException($"Row '{layout.RowTags[rows.Count]}' was removed; adding or removing rows is not supported.");
    }

    private static InvalidOperationException WrongType(string tag, Ff7rField field) =>
        new($"Row '{tag}' field '{field.Name}': the value is no longer a {field.Type}{(field.IsArray ? " array" : string.Empty)}.");

    private static void RequireKnownName(UAsset asset, Ff7rTableLayout layout, FName? name, string tag, string fieldName)
    {
        if (name is null || name.IsDummy || name.Value is not { } text || !asset.ContainsNameReference(text) || asset.SearchNameReference(text) >= layout.NameCountAtLoad)
            throw new InvalidOperationException($"Row '{tag}' field '{fieldName}': name '{FNameDisplay.ToDisplayString(name)}' is not in the package's name table; adding names is not supported.");
    }

    private static void WriteScalar(AssetBinaryWriter writer, UAsset asset, Ff7rTableLayout layout, PropertyData value, Ff7rField field, string tag, bool inArray, bool originalUtf16)
    {
        switch (field.Type, value)
        {
            case (Ff7rFieldType.Boolean, BoolPropertyData b):
                if (inArray) writer.Write((byte)(b.Value ? 1 : 0));
                else writer.Write(b.Value ? 1 : 0);
                break;
            case (Ff7rFieldType.BooleanByte, BoolPropertyData b):
                writer.Write((byte)(b.Value ? 1 : 0));
                break;
            case (Ff7rFieldType.Byte, BytePropertyData { ByteType: BytePropertyType.Byte } by):
                writer.Write(by.Value);
                break;
            case (Ff7rFieldType.UInt16, UInt16PropertyData u):
                writer.Write(u.Value);
                break;
            case (Ff7rFieldType.Int32, IntPropertyData i):
                writer.Write(i.Value);
                break;
            case (Ff7rFieldType.Float, FloatPropertyData fl):
                writer.Write(fl.Value);
                break;
            case (Ff7rFieldType.String, StrPropertyData s):
                WriteString(writer, s.Value, originalUtf16 && !inArray);
                break;
            case (Ff7rFieldType.Name, NamePropertyData n):
                RequireKnownName(asset, layout, n.Value, tag, field.Name);
                writer.Write(n.Value);
                break;
            default:
                throw WrongType(tag, field);
        }
    }

    private static void WriteString(AssetBinaryWriter writer, FString? value, bool keepUtf16)
    {
        if (value?.Value is not { } text)
        {
            writer.Write(0);
            return;
        }

        if (keepUtf16 || value.Encoding is UnicodeEncoding)
        {
            writer.Write(-(text.Length + 1));
            writer.Write(LenientUtf16.GetBytes(text));
            writer.Write((short)0);
        }
        else
        {
            var bytes = LenientUtf8.GetBytes(text);
            writer.Write(bytes.Length + 1);
            writer.Write(bytes);
            writer.Write((byte)0);
        }
    }

    /// <summary>A forward-only reader that refuses to read past the end of the body.</summary>
    private sealed class Cursor(byte[] buffer)
    {
        public int Position { get; private set; }

        public int Remaining => buffer.Length - Position;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Remaining) throw new InvalidDataException($"truncated at offset {Position}: needs {count} more bytes, {Remaining} left");
            var span = buffer.AsSpan(Position, count);
            Position += count;
            return span;
        }

        public byte Byte() => Take(1)[0];

        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(sizeof(ushort)));

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));

        public byte[] Bytes(int count) => Take(count).ToArray();

        public (int Index, int Number) NameReference(int nameCount)
        {
            var index = Int32();
            var number = Int32();
            if (index < 0 || index >= nameCount) throw new InvalidDataException($"name index {index} is outside the name table of {nameCount}");
            if (number < 0) throw new InvalidDataException($"name number {number} is negative");
            return (index, number);
        }

        public FString? String(out bool utf16)
        {
            utf16 = false;
            var length = Int32();
            if (length == 0) return null;
            if (length == int.MinValue) throw new InvalidDataException("string length is out of range");
            if (length > 0)
            {
                var data = Take(length);
                if (data[^1] != 0) throw new InvalidDataException("string is not null terminated");
                return new FString(StrictUtf8.GetString(data[..^1]), Encoding.UTF8);
            }

            utf16 = true;
            var units = Take(checked(-length * 2));
            if (units[^1] != 0 || units[^2] != 0) throw new InvalidDataException("string is not null terminated");
            return new FString(StrictUtf16.GetString(units[..^2]), Encoding.Unicode);
        }
    }
}
