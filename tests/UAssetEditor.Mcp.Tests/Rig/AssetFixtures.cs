using System.Reflection;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Tests;

namespace UAssetEditor.Mcp.Tests.Rig;

/// <summary>Real .uasset/.uexp files on disk, written from the in-memory assets the Core tests build, so the tools open
/// them the way they open a game's files. The sample export is "TestExport" (export 0) with bEnabled=true, Count=5,
/// DisplayName="Hello World", Location{X=1.5,Y=2.5} and Tags=[Alpha,Beta].</summary>
internal static class AssetFixtures
{
    public const string SampleExportName = "TestExport";

    public static string WriteSample(TempFolder folder, string fileName = "Sample.uasset", Action<UAsset>? customise = null)
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset, SampleExportName);
        asset.Imports.Add(new Import("/Script/Engine", "Package", FPackageIndex.FromRawIndex(0), "/Game/Textures/T_Wall", false, asset));
        asset.Imports.Add(new Import("/Script/Engine", "Texture2D", FPackageIndex.FromImport(0), "T_Wall", false, asset));

        // A hand-built package has none of the header state a read one gets from its file.
        asset.Generations = [new FGenerationInfo(1, 1)];
        asset.RecordedEngineVersion = new FEngineVersion(4, 27, 0, 0, FString.FromString(""));
        asset.RecordedCompatibleWithEngineVersion = asset.RecordedEngineVersion;
        asset.FolderName = FString.FromString("None");
        asset.PackageGuid = Guid.NewGuid();
        asset.PackageSource = 0;
        asset.PackageFlags = EPackageFlags.PKG_FilterEditorOnly;
        asset.DependsMap = [];
        asset.SoftPackageReferenceList = [];
        // Internal to UAssetAPI, which only ever fills them from a file.
        SetInternal(asset, "AdditionalPackagesToCook", new List<FString>());
        SetInternal(asset, "doWeHaveAssetRegistryData", false);
        SetInternal(asset, "doWeHaveWorldTileInfo", false);
        asset.ChunkIDs = [];
        asset.LegacyFileVersion = -7;

        // The writer refuses to add a name while serializing, so the property type names are in the table up front.
        foreach (var name in (string[])["None", "BoolProperty", "IntProperty", "StrProperty", "StructProperty", "ArrayProperty", "FloatProperty", "NameProperty", "PointData"])
            asset.AddNameReference(FString.FromString(name));

        // "Vector" is a native struct that holds exactly three fields; a plain struct keeps the fixture a table of fields.
        export.Data.OfType<StructPropertyData>().Single(p => p.Name.ToString() == "Location").StructType = new FName(asset, "PointData");

        // A hand-built export has no links; a written package needs all four.
        export.ClassIndex = FPackageIndex.FromImport(1);
        export.OuterIndex = FPackageIndex.FromRawIndex(0);
        export.SuperIndex = FPackageIndex.FromRawIndex(0);
        export.TemplateIndex = FPackageIndex.FromRawIndex(0);
        export.Extras = [];
        asset.AdditionalFiles = [];
        asset.UseSeparateBulkDataFiles = true; // a cooked package: export data in the .uexp
        customise?.Invoke(asset);

        var path = folder.Combine(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PackageWriter.Write(asset, path);
        return path;
    }

    private static void SetInternal(UAsset asset, string field, object value) =>
        typeof(UAsset).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(asset, value);

    /// <summary>Opens a written fixture the way the tools do: versioned UE4.27, no usmap.</summary>
    public static UAsset Reopen(string path) => ResilientAssetLoader.OpenStrict(path, EngineVersion.VER_UE4_27, mappings: null, UAssetEditor.Core.Games.Game.None);

    public static string ReadProperty(string path, string propertyPath)
    {
        var asset = Reopen(path);
        var node = UAssetEditor.Core.PropertyAccess.PropertyLocator.Locate(asset, 0, propertyPath)
            ?? throw new InvalidOperationException($"No property {propertyPath} in {path}.");
        return UAssetEditor.Core.PropertyAccess.PropertyValueAccessor.AsSearchableString(node.Property, asset) ?? "";
    }

    /// <summary>The bytes of the package's files, to prove a call wrote nothing.</summary>
    public static byte[][] Snapshot(string uassetPath) =>
        [File.ReadAllBytes(uassetPath), File.ReadAllBytes(Path.ChangeExtension(uassetPath, ".uexp"))];
}
