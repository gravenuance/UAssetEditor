using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// A modded-layer conversion resolves through the whole Paks folder, so retoc also writes the base game's bulk file for a
/// package the mod replaces without shipping its bulk data. Only what the mod ships may stay in the output.
/// </summary>
public sealed class IoStoreLegacyConverterTests : IDisposable
{
    private const string MeshDir = "Marvel/Content/Marvel/Characters/1048/1048500/Meshes";
    private const string TexDir = "Marvel/Content/Marvel/Characters/1048/1048500/Textures";

    private readonly string _output = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_LegacyBulk_" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_output)) Directory.Delete(_output, recursive: true);
    }

    private void Write(string relativePath)
    {
        var path = Path.Combine(_output, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    private bool Exists(string relativePath) => File.Exists(Path.Combine(_output, relativePath));

    // retoc reads the container's files (a Marvel Rivals one's companion .pak among them) while it writes the output.
    [Theory]
    [InlineData("Mod_P.pak", null, ".pak")]
    [InlineData("MOD_P.PAK", null, ".pak")]
    [InlineData("Linked.pak", "Mod_P.pak", ".pak")]
    [InlineData("Linked.pak", "Mod_P.ucas", ".ucas")]
    [InlineData("Linked.pak", "Mod_P.utoc", ".utoc")]
    public async Task ConvertAsync_RefusesAPakOutputThatIsOneOfTheContainersOwnFiles_BeforeTouchingIt(string outputName, string? hardLinkTo, string own)
    {
        foreach (var file in (string[])["Mod_P.utoc", "Mod_P.ucas", "Mod_P.pak"]) Write(file);
        var utoc = Path.Combine(_output, "Mod_P.utoc");
        var output = Path.Combine(_output, outputName);
        if (hardLinkTo != null) TestLinks.MakeHardLink(output, Path.Combine(_output, hardLinkTo));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => IoStoreLegacyConverter.ConvertAsync(
            utoc, output, RetocLayer.Modded, [], aesKey: null, paksFolder: null, TestContext.Current.CancellationToken));

        Assert.Equal("output", refused.ParamName);
        Assert.Contains($"container's own {own},", refused.Message, StringComparison.Ordinal);
        Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(_output, "Mod_P" + own), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RemoveBulkFilesNotShipped_DeletesBaseGameBulkOfAReplacedPackage()
    {
        Write($"{MeshDir}/SK_1048_1048500.uasset");
        Write($"{MeshDir}/SK_1048_1048500.uexp");
        Write($"{MeshDir}/SK_1048_1048500.ubulk");
        Write($"{MeshDir}/SK_1048_1048500.uptnl");
        Write($"{MeshDir}/SK_1048_1048500.m.ubulk");

        var removed = IoStoreLegacyConverter.RemoveBulkFilesNotShipped(_output, [$"../../../{MeshDir}/SK_1048_1048500.uasset"]);

        Assert.Equal(
            [$"{MeshDir}/SK_1048_1048500.m.ubulk", $"{MeshDir}/SK_1048_1048500.ubulk", $"{MeshDir}/SK_1048_1048500.uptnl"],
            removed.Order(StringComparer.Ordinal));
        Assert.True(Exists($"{MeshDir}/SK_1048_1048500.uasset"));
        Assert.True(Exists($"{MeshDir}/SK_1048_1048500.uexp"));
        Assert.False(Exists($"{MeshDir}/SK_1048_1048500.ubulk"));
    }

    [Fact]
    public void RemoveBulkFilesNotShipped_KeepsBulkTheContainerShips_IgnoringCase()
    {
        Write($"{TexDir}/T_Body_D.uasset");
        Write($"{TexDir}/T_Body_D.ubulk");

        var removed = IoStoreLegacyConverter.RemoveBulkFilesNotShipped(_output,
            [$"../../../{TexDir}/T_Body_D.uasset", $"../../../{TexDir.ToUpperInvariant()}/T_BODY_D.ubulk"]);

        Assert.Empty(removed);
        Assert.True(Exists($"{TexDir}/T_Body_D.ubulk"));
    }

    [Fact]
    public void RemoveBulkFilesNotShipped_KeepsBulkOfPackagesTheContainerDoesNotShip()
    {
        // A base-game entry asked for by filter: the base game's bulk is that entry's own
        Write($"{TexDir}/T_Vanilla_D.uasset");
        Write($"{TexDir}/T_Vanilla_D.ubulk");

        var removed = IoStoreLegacyConverter.RemoveBulkFilesNotShipped(_output, [$"../../../{TexDir}/T_Other_D.uasset"]);

        Assert.Empty(removed);
        Assert.True(Exists($"{TexDir}/T_Vanilla_D.ubulk"));
    }
}
