using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class SparseClassDataTests
{
    [Fact]
    public void SaveWithoutChanges_ReproducesTheOriginalBytes()
    {
        var asset = TestAssets.CreateAsset();
        var cdo = new NormalExport(asset, []) { ObjectName = new FName(asset, "Default__Graph_C"), Data = [] };
        asset.Exports.Add(cdo);
        TestAssets.AddSparseHandlers(asset, cdo, "None", "EvaluateAlpha");
        var original = cdo.Extras;

        SparseClassData.Read(asset, cdo)!.Save();

        Assert.Equal(original, cdo.Extras);
    }

    [Fact]
    public void Read_ReturnsNullForACdoWithoutSparseData()
    {
        var asset = TestAssets.CreateAsset();
        var cdo = new NormalExport(asset, []) { ObjectName = new FName(asset, "Default__Graph_C"), Data = [] };
        asset.Exports.Add(cdo);

        Assert.Null(SparseClassData.Read(asset, cdo));
    }
}
