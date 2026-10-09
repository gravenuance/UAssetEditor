using System.Text;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class StringTableEditorTests
{
    private static StringTableExport AddTable(UAssetAPI.UAsset asset)
    {
        var table = new FStringTable(new FString("ItemStringTable"));
        // Keys as a file read leaves them: ASCII-encoded, which plain-string FStrings don't equal.
        table.Add(new FString("Item_A_Name", Encoding.ASCII), new FString("에이", Encoding.Unicode));
        var export = new StringTableExport(table, asset, []);
        asset.Exports.Add(export);
        return export;
    }

    [Fact]
    public void Set_ReplacesAnExistingKeysValue_AndReturnsTheOldOne()
    {
        var asset = TestAssets.CreateAsset();
        var export = AddTable(asset);

        var previous = StringTableEditor.Set(asset, 0, "Item_A_Name", "Alpha");

        Assert.Equal("에이", previous);
        Assert.Equal(1, export.Table.Count);
        Assert.Equal("Alpha", export.Table[0].Value);
    }

    [Fact]
    public void Set_AppendsAMissingKey_AndReturnsNull()
    {
        var asset = TestAssets.CreateAsset();
        var export = AddTable(asset);

        var previous = StringTableEditor.Set(asset, 0, "Item_B_Name", "Bravo");

        Assert.Null(previous);
        Assert.Equal(["Item_A_Name", "Item_B_Name"], export.Table.Keys.Select(k => k.Value));
        Assert.Equal("Bravo", export.Table[1].Value);
    }

    [Fact]
    public void Set_StoresNonAsciiTextAsUtf16_SoItSurvivesTheWrite()
    {
        var asset = TestAssets.CreateAsset();
        var export = AddTable(asset);

        StringTableEditor.Set(asset, 0, "Item_A_Name", "Gear für Tempo");
        StringTableEditor.Set(asset, 0, "Item_B_Name", "Speed Gear");

        Assert.Equal(Encoding.Unicode, export.Table[0].Encoding);
        Assert.Equal(Encoding.UTF8, export.Table[1].Encoding);
    }

    [Fact]
    public void Set_RefusesAnExportThatIsNotAStringTable()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleDataTableExport(asset);

        Assert.Throws<ArgumentException>(() => StringTableEditor.Set(asset, 0, "K", "V"));
    }
}
