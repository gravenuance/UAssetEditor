using UAssetEditor.Core.AssetSources;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.Games.Ff7r;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests.Games;

public class Ff7rDataObjectTests
{
    // Name map positions used by the hand-written table below.
    private static readonly string[] Names =
    [
        "RowA", "RowB", "F_Bool", "F_Byte", "F_BoolByte", "F_U16", "F_Int", "F_Float", "F_Str", "F_Name", "F_Bool_Array", "F_Str_Array", "Extra",
    ];

    // Two rows, ten columns: every field type once, plus a BOOLEAN array and a string array.
    private const string TableHex =
        "00 01 00 00 00 00 02 00 00 00 " +                       // header, kept verbatim
        "02 00 00 00  0A 00 00 00 " +                            // 2 rows, 10 fields
        "02 00 00 00 00 00 00 00 01 " +                          // F_Bool        BOOLEAN
        "03 00 00 00 00 00 00 00 02 " +                          // F_Byte        BYTE
        "04 00 00 00 00 00 00 00 03 " +                          // F_BoolByte    BOOLEAN_BYTE
        "05 00 00 00 00 00 00 00 04 " +                          // F_U16         UINT16
        "06 00 00 00 00 00 00 00 07 " +                          // F_Int         INT32
        "07 00 00 00 00 00 00 00 09 " +                          // F_Float       FLOAT
        "08 00 00 00 00 00 00 00 0A " +                          // F_Str         STRING
        "09 00 00 00 00 00 00 00 0B " +                          // F_Name        NAME
        "0A 00 00 00 00 00 00 00 01 " +                          // F_Bool_Array  BOOLEAN array
        "0B 00 00 00 00 00 00 00 0A " +                          // F_Str_Array   STRING array
        // row A: tag RowA#0
        "00 00 00 00 00 00 00 00 " +
        "01 00 00 00 " +                                         // F_Bool = true (4 bytes as a scalar)
        "07 " +                                                  // F_Byte = 7
        "01 " +                                                  // F_BoolByte = true
        "34 12 " +                                               // F_U16 = 0x1234
        "2A 00 00 00 " +                                         // F_Int = 42
        "00 00 C0 3F " +                                         // F_Float = 1.5
        "03 00 00 00 48 69 00 " +                                // F_Str = "Hi"
        "0C 00 00 00 03 00 00 00 " +                             // F_Name = Extra#3
        "02 00 00 00 01 00 " +                                   // F_Bool_Array = [true, false] (1 byte each)
        "01 00 00 00 03 00 00 00 59 6F 00 " +                    // F_Str_Array = ["Yo"]
        // row B: tag RowB#2
        "01 00 00 00 02 00 00 00 " +
        "00 00 00 00 " +                                         // F_Bool = false
        "FF " +                                                  // F_Byte = 255
        "00 " +                                                  // F_BoolByte = false
        "FF FF " +                                               // F_U16 = 65535
        "FF FF FF FF " +                                         // F_Int = -1
        "00 00 20 C1 " +                                         // F_Float = -10
        "FE FF FF FF E9 00 00 00 " +                             // F_Str = UTF-16 e-acute
        "0C 00 00 00 00 00 00 00 " +                             // F_Name = Extra#0
        "00 00 00 00 " +                                         // F_Bool_Array = []
        "01 00 00 00 00 00 00 00";                               // F_Str_Array = [null]

    private static byte[] Hex(string text) =>
        Convert.FromHexString(text.Replace(" ", string.Empty, StringComparison.Ordinal));

    private static (UAsset Asset, byte[] Body) CreateTable(string hex = TableHex, string className = "EndDataObjectTest")
    {
        var body = Hex(hex);
        var asset = TestAssets.CreateAsset();
        foreach (var name in Names) asset.AddNameReference(new FString(name));
        asset.Imports.Add(new Import("/Script/End", "Class", FPackageIndex.FromRawIndex(0), className, false, asset));
        asset.Exports.Add(new RawExport(body, asset, [])
        {
            ObjectName = new FName(asset, "Table"),
            ClassIndex = FPackageIndex.FromImport(0),
            SerialSize = body.Length,
        });
        return (asset, body);
    }

    private static byte[] Write(UAsset asset)
    {
        using var stream = new MemoryStream();
        using var writer = new AssetBinaryWriter(stream, Encoding.UTF8, leaveOpen: true, asset);
        asset.Exports[0].Write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    private static (UAsset Asset, byte[] Body, Ff7rDataObjectExport Table) Decode(string hex = TableHex)
    {
        var (asset, body) = CreateTable(hex);
        var warnings = GameProfile.For(Game.FinalFantasy7Remake)!.PostOpen(asset);
        Assert.Empty(warnings);
        return (asset, body, Assert.IsType<Ff7rDataObjectExport>(asset.Exports[0]));
    }

    private static PropertyData Cell(UAsset asset, string path) =>
        PropertyLocator.Locate(asset, 0, path)!.Property;

    [Fact]
    public void Install_DecodesRowsAndFieldsIntoProperties()
    {
        var (asset, _, table) = Decode();

        Assert.Equal(2, table.Data.Count);
        var rowA = Assert.IsType<StructPropertyData>(table.Data[0]);
        Assert.Equal("RowA", rowA.Name.Value.Value);
        var rowB = Assert.IsType<StructPropertyData>(table.Data[1]);
        Assert.Equal("RowB", rowB.Name.Value.Value);
        Assert.Equal(2, rowB.Name.Number);

        Assert.True(Assert.IsType<BoolPropertyData>(Cell(asset, "RowA.F_Bool")).Value);
        Assert.Equal((byte)7, Assert.IsType<BytePropertyData>(Cell(asset, "RowA.F_Byte")).Value);
        Assert.True(Assert.IsType<BoolPropertyData>(Cell(asset, "RowA.F_BoolByte")).Value);
        Assert.Equal((ushort)0x1234, Assert.IsType<UInt16PropertyData>(Cell(asset, "RowA.F_U16")).Value);
        Assert.Equal(42, Assert.IsType<IntPropertyData>(Cell(asset, "RowA.F_Int")).Value);
        Assert.Equal(1.5f, Assert.IsType<FloatPropertyData>(Cell(asset, "RowA.F_Float")).Value);
        Assert.Equal("Hi", Assert.IsType<StrPropertyData>(Cell(asset, "RowA.F_Str")).Value?.Value);
        var name = Assert.IsType<NamePropertyData>(Cell(asset, "RowA.F_Name")).Value;
        Assert.Equal("Extra", name.Value.Value);
        Assert.Equal(3, name.Number);
        Assert.Equal(2, Assert.IsType<ArrayPropertyData>(Cell(asset, "RowA.F_Bool_Array")).Value.Length);
        Assert.False(Assert.IsType<BoolPropertyData>(Cell(asset, "RowA.F_Bool_Array[1]")).Value);
        Assert.Equal("Yo", Assert.IsType<StrPropertyData>(Cell(asset, "RowA.F_Str_Array[0]")).Value?.Value);

        Assert.Equal((byte)255, Assert.IsType<BytePropertyData>(Cell(asset, "RowB_1.F_Byte")).Value);
        Assert.Equal(-1, Assert.IsType<IntPropertyData>(Cell(asset, "RowB_1.F_Int")).Value);
        Assert.Equal(-10f, Assert.IsType<FloatPropertyData>(Cell(asset, "RowB_1.F_Float")).Value);
        Assert.Equal("é", Assert.IsType<StrPropertyData>(Cell(asset, "RowB_1.F_Str")).Value?.Value);
        Assert.Empty(Assert.IsType<ArrayPropertyData>(Cell(asset, "RowB_1.F_Bool_Array")).Value);
        Assert.Null(Assert.IsType<StrPropertyData>(Cell(asset, "RowB_1.F_Str_Array[0]")).Value?.Value);
    }

    [Fact]
    public void Write_WithoutEdits_ReproducesTheBodyByteForByte()
    {
        var (asset, body, _) = Decode();

        Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(Write(asset)));
    }

    [Fact]
    public void Write_AfterSameSizeEdits_ChangesOnlyTheEditedBytes()
    {
        var (asset, _, _) = Decode();

        Assert.True(PropertyValueAccessor.TrySetStringValue(Cell(asset, "RowA.F_Int"), "258", asset));
        Assert.True(PropertyValueAccessor.TrySetStringValue(Cell(asset, "RowA.F_Str"), "Ok", asset));
        Assert.True(PropertyValueAccessor.TrySetStringValue(Cell(asset, "RowA.F_Bool_Array[1]"), "true", asset));
        Assert.True(PropertyValueAccessor.TrySetStringValue(Cell(asset, "RowB_1.F_Str"), "è", asset));

        // F_Int 42 -> 258 (02 01 00 00), "Hi" -> "Ok", the second array bool 00 -> 01, UTF-16 e-acute -> e-grave (E8 00).
        var expected = TableHex
            .Replace("2A 00 00 00 ", "02 01 00 00 ", StringComparison.Ordinal)
            .Replace("48 69 00", "4F 6B 00", StringComparison.Ordinal)
            .Replace("02 00 00 00 01 00 ", "02 00 00 00 01 01 ", StringComparison.Ordinal)
            .Replace("FE FF FF FF E9 00 00 00", "FE FF FF FF E8 00 00 00", StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(Hex(expected)), Convert.ToHexString(Write(asset)));
    }

    [Fact]
    public void Write_AfterRepointingAName_KeepsTheNumber()
    {
        var (asset, _, _) = Decode();

        var cell = Assert.IsType<NamePropertyData>(Cell(asset, "RowA.F_Name"));
        cell.Value = new FName(asset, 1, 5); // RowB#5: a name already in the map

        var expected = TableHex.Replace("0C 00 00 00 03 00 00 00 ", "01 00 00 00 05 00 00 00 ", StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(Hex(expected)), Convert.ToHexString(Write(asset)));
    }

    [Fact]
    public void Write_RejectsAStringLengthChange_NamingRowAndField()
    {
        var (asset, _, _) = Decode();
        Assert.True(PropertyValueAccessor.TrySetStringValue(Cell(asset, "RowB_1.F_Str"), "longer", asset));

        var error = Assert.Throws<InvalidOperationException>(() => Write(asset));

        Assert.Contains("Row 'RowB' field 'F_Str'", error.Message, StringComparison.Ordinal);
        Assert.Contains("only same-size edits", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RejectsAnArrayLengthChange()
    {
        var (asset, _, table) = Decode();
        var array = Assert.IsType<ArrayPropertyData>(Cell(asset, "RowA.F_Bool_Array"));
        array.Value = [array.Value[0]];

        var error = Assert.Throws<InvalidOperationException>(() => Write(asset));

        Assert.Equal("Row 'RowA' field 'F_Bool_Array': array length changed from 2 to 1; only same-size edits can be saved.", error.Message);
        Assert.Equal(2, table.Data.Count);
    }

    [Fact]
    public void Write_RejectsRemovedAndAddedRows()
    {
        var (asset, _, table) = Decode();
        var removed = table.Data[1];
        table.Data.RemoveAt(1);

        var removedError = Assert.Throws<InvalidOperationException>(() => Write(asset));
        Assert.Contains("'RowB' was removed", removedError.Message, StringComparison.Ordinal);

        table.Data.Add(removed);
        table.Data.Add(new StructPropertyData(new FName(asset, "RowA")) { Value = [] });
        var addedError = Assert.Throws<InvalidOperationException>(() => Write(asset));
        Assert.Contains("added", addedError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RejectsANameThatIsNotInTheNameMap()
    {
        var (asset, _, _) = Decode();
        var cell = Assert.IsType<NamePropertyData>(Cell(asset, "RowA.F_Name"));
        cell.Value = new FName(asset, "BrandNewName");

        var error = Assert.Throws<InvalidOperationException>(() => Write(asset));

        Assert.Equal("Row 'RowA' field 'F_Name': name 'BrandNewName' is not in the package's name table; adding names is not supported.", error.Message);
    }

    [Theory]
    [InlineData("unknown type byte", "0B 00 00 00 00 00 00 00 0A ", "0B 00 00 00 00 00 00 00 63 ")]
    [InlineData("absurd row count", "02 00 00 00  0A 00 00 00 ", "FF FF FF 7F  0A 00 00 00 ")]
    [InlineData("absurd field count", "02 00 00 00  0A 00 00 00 ", "02 00 00 00  FF FF FF 7F ")]
    [InlineData("negative count", "02 00 00 00  0A 00 00 00 ", "FF FF FF FF  0A 00 00 00 ")]
    [InlineData("absurd array count", "02 00 00 00 01 00 ", "FF FF FF 7F 01 00 ")]
    [InlineData("name index outside the map", "0C 00 00 00 03 00 00 00 ", "63 00 00 00 03 00 00 00 ")]
    [InlineData("boolean that is neither 0 nor 1", "01 00 00 00 07 ", "05 00 00 00 07 ")]
    public void Install_LeavesAnUndecodableTableAsItWas_AndWarns(string reason, string find, string replace)
    {
        var hex = TableHex.Replace(find, replace, StringComparison.Ordinal);
        Assert.NotEqual(TableHex, hex);
        var (asset, _) = CreateTable(hex);

        var warnings = GameProfile.For(Game.FinalFantasy7Remake)!.PostOpen(asset);

        Assert.True(warnings.Count == 1, reason);
        Assert.Contains("export 1 (EndDataObjectTest) was left undecoded", warnings[0], StringComparison.Ordinal);
        Assert.IsType<RawExport>(asset.Exports[0]);
    }

    [Theory]
    [InlineData("03")]
    [InlineData("0B")]
    public void Install_RefusesMoreRowsThanTheBodyCanHold_BeforeAllocatingThem(string rowCountByte)
    {
        var (asset, _) = CreateTable(TableHex.Replace("02 00 00 00  0A 00 00 00 ", $"{rowCountByte} 00 00 00  0A 00 00 00 ", StringComparison.Ordinal));

        var warning = Assert.Single(GameProfile.For(Game.FinalFantasy7Remake)!.PostOpen(asset));

        Assert.Contains("rows of at least", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderWarnings_StayWithTheOpenedAsset()
    {
        var (undecodable, _) = CreateTable(TableHex.Replace("01 00 00 00 07 ", "05 00 00 00 07 ", StringComparison.Ordinal));
        var (clean, _) = CreateTable();

        ResilientAssetLoader.PostOpen(undecodable, Game.FinalFantasy7Remake);
        ResilientAssetLoader.PostOpen(clean, Game.FinalFantasy7Remake);

        var warning = Assert.Single(ResilientAssetLoader.WarningsFor(undecodable));
        Assert.Contains("was left undecoded", warning, StringComparison.Ordinal);
        Assert.Empty(ResilientAssetLoader.WarningsFor(clean));
    }

    [Fact]
    public void Install_LeavesATruncatedOrOverlongBodyAlone()
    {
        foreach (var hex in new[] { TableHex[..^6], TableHex + " 00" })
        {
            var (asset, _) = CreateTable(hex);

            var warnings = GameProfile.For(Game.FinalFantasy7Remake)!.PostOpen(asset);

            Assert.Single(warnings);
            Assert.IsType<RawExport>(asset.Exports[0]);
        }
    }

    [Fact]
    public void Install_IgnoresExportsThatAreNotDataObjects()
    {
        var (asset, _) = CreateTable(className: "EndSomethingElse");

        var warnings = GameProfile.For(Game.FinalFantasy7Remake)!.PostOpen(asset);

        Assert.Empty(warnings);
        Assert.IsType<RawExport>(asset.Exports[0]);
    }

    [Theory]
    [InlineData(Game.None)]
    [InlineData(Game.MarvelRivals)]
    public void PostOpen_LeavesTheTableUndecodedForOtherGames(Game game)
    {
        var (asset, _) = CreateTable();

        var warnings = GameProfile.For(game)?.PostOpen(asset) ?? [];

        Assert.Empty(warnings);
        Assert.IsType<RawExport>(asset.Exports[0]);
    }
}
