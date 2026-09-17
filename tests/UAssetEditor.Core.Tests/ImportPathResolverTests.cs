using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class ImportPathResolverTests
{
    private static Import AddImport(UAsset asset, string objectName, FPackageIndex outerIndex) =>
        new("/Script/Engine", "Texture2D", outerIndex, objectName, false, asset);

    [Fact]
    public void GetFullPath_JoinsOuterChainWithDots()
    {
        var asset = TestAssets.CreateAsset();
        var package = AddImport(asset, "/Game/Textures/T_Wall", FPackageIndex.FromRawIndex(0));
        asset.Imports.Add(package);
        var texture = AddImport(asset, "T_Wall", FPackageIndex.FromImport(0));
        asset.Imports.Add(texture);

        Assert.Equal("/Game/Textures/T_Wall.T_Wall", ImportPathResolver.GetFullPath(texture, asset));
    }

    [Fact]
    public void FindImportIndex_FindsExactFullPathMatch()
    {
        var asset = TestAssets.CreateAsset();
        asset.Imports.Add(AddImport(asset, "/Game/Textures/T_Wall", FPackageIndex.FromRawIndex(0)));
        asset.Imports.Add(AddImport(asset, "T_Wall", FPackageIndex.FromImport(0)));

        var found = ImportPathResolver.FindImportIndex(asset, "/Game/Textures/T_Wall.T_Wall");

        Assert.Equal(1, found);
    }

    [Fact]
    public void FindImportIndex_ReturnsNullWhenNoImportMatches()
    {
        var asset = TestAssets.CreateAsset();
        asset.Imports.Add(AddImport(asset, "/Game/Textures/T_Wall", FPackageIndex.FromRawIndex(0)));
        asset.Imports.Add(AddImport(asset, "T_Wall", FPackageIndex.FromImport(0)));

        var found = ImportPathResolver.FindImportIndex(asset, "/Game/Textures/T_DoesNotExist.T_DoesNotExist");

        Assert.Null(found);
    }
}
