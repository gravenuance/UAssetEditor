using ModelContextProtocol;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Tests;
using UAssetEditor.Mcp.Tests.Rig;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tests;

public sealed class AssetReadToolTests : IDisposable
{
    private readonly McpRig _rig = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _rig.Dispose();

    private string Sample(Action<UAsset>? customise = null, string name = "Sample.uasset") =>
        AssetFixtures.WriteSample(_rig.Folder, name, customise);

    [Fact]
    public async Task ListExports_ListsTheExportWithItsIndexNameAndType()
    {
        var result = await _rig.Assets.ListExportsAsync(Sample(), cancellationToken: _ct);

        Assert.Equal(1, result.Total);
        Assert.False(result.Truncated);
        var export = Assert.Single(result.Exports);
        Assert.Equal(0, export.Index);
        Assert.Equal("TestExport", export.Name);
        Assert.Equal("NormalExport", export.Type);
        Assert.Null(export.Members);
    }

    [Fact]
    public async Task ListExports_WithAMissingFile_SaysSo()
    {
        var missing = _rig.Folder.Combine("Nothing.uasset");

        var error = await Assert.ThrowsAsync<McpException>(() => _rig.Assets.ListExportsAsync(missing, cancellationToken: _ct));

        Assert.Contains("not found", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sample.uasset")]
    [InlineData("..\\Sample.uasset")]
    [InlineData("\\\\evil.example\\share\\Sample.uasset")]
    [InlineData("//evil.example/share/Sample.uasset")]
    public async Task ListExports_RefusesRelativeAndNetworkPaths(string path)
    {
        await Assert.ThrowsAnyAsync<McpException>(() => _rig.Assets.ListExportsAsync(path, cancellationToken: _ct));
    }

    [Fact]
    public async Task ListExports_RefusesAFileThatIsNotAnAsset()
    {
        var text = _rig.Folder.Combine("notes.txt");
        await File.WriteAllTextAsync(text, "x", _ct);

        var error = await Assert.ThrowsAsync<McpException>(() => _rig.Assets.ListExportsAsync(text, cancellationToken: _ct));

        Assert.Contains(".uasset", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListExports_WithAnUnknownGameOrEngineVersion_NamesTheValidChoices()
    {
        var path = Sample();

        var game = await Assert.ThrowsAsync<McpException>(() => _rig.Assets.ListExportsAsync(path, game: "halflife", cancellationToken: _ct));
        var version = await Assert.ThrowsAsync<McpException>(() => _rig.Assets.ListExportsAsync(path, engineVersion: "5", cancellationToken: _ct));

        Assert.Contains("rivals", game.Message, StringComparison.Ordinal);
        Assert.Contains("VER_UE5_3", version.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListExports_WhenCancelled_Throws()
    {
        var path = Sample();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Assets.ListExportsAsync(path, cancellationToken: McpRig.Cancelled));
    }

    [Fact]
    public async Task ListExports_ForAGameWithNoStoredUsmap_WarnsAndSaysWhatToDo()
    {
        var result = await _rig.Assets.ListExportsAsync(Sample(), game: "rivals", engineVersion: "VER_UE4_27", cancellationToken: _ct);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("No usmap is stored for rivals", warning, StringComparison.Ordinal);
        Assert.Contains("usmap_add", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListImports_ListsEachImportsFullPathAndClass()
    {
        var result = await _rig.Assets.ListImportsAsync(Sample(), cancellationToken: _ct);

        Assert.Equal(2, result.Total);
        Assert.Equal(["/Game/Textures/T_Wall", "/Game/Textures/T_Wall.T_Wall"], result.Imports.Select(i => i.Path));
        Assert.Equal("Texture2D", result.Imports[1].Class);
    }

    [Fact]
    public async Task GetTree_ShowsTheStructAndArrayNodesWithTheirPaths()
    {
        var result = await _rig.Assets.GetTreeAsync(Sample(), "0", depth: 1, cancellationToken: _ct);

        Assert.Equal(["Location", "Tags"], result.Nodes.Select(n => n.Path));
        Assert.All(result.Nodes, node => Assert.Equal(0, node.Depth));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task GetTree_ResolvesTheExportByName()
    {
        var result = await _rig.Assets.GetTreeAsync(Sample(), "TestExp", depth: 1, cancellationToken: _ct);

        Assert.Equal(0, result.Export);
    }

    [Fact]
    public async Task GetTree_UnderAnArray_ListsItsElements()
    {
        var path = Sample(asset => TestAssets.AddStructArray(asset, (NormalExport)asset.Exports[0], "Items", 10, 20, 30));

        var result = await _rig.Assets.GetTreeAsync(path, "0", path: "Items", depth: 1, cancellationToken: _ct);

        Assert.Equal(["Items[0]", "Items[1]", "Items[2]"], result.Nodes.Select(n => n.Path));
    }

    [Fact]
    public async Task GetTree_RefusesADepthOutsideTheRangeAndAPathThatIsNotThere()
    {
        var path = Sample();

        await Assert.ThrowsAsync<McpException>(() => _rig.Assets.GetTreeAsync(path, "0", depth: 0, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.Assets.GetTreeAsync(path, "0", depth: 11, cancellationToken: _ct));
        var missing = await Assert.ThrowsAsync<McpException>(() => _rig.Assets.GetTreeAsync(path, "0", path: "Nope.Nothing", cancellationToken: _ct));
        Assert.Contains("No property at path", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DumpExport_ListsEveryLeafPropertyAsPathAndValue()
    {
        var result = await _rig.Assets.DumpExportAsync(Sample(), "0", cancellationToken: _ct);

        var values = result.Properties.ToDictionary(p => p.Path, p => p.Value);
        Assert.Equal("5", values["Count"]);
        Assert.Equal("Hello World", values["DisplayName"]);
        Assert.Equal("1.5", values["Location.X"]);
        Assert.Equal("Alpha", values["Tags[0]"]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task DumpExport_WithAFilter_KeepsOnlyMatchingPaths()
    {
        var result = await _rig.Assets.DumpExportAsync(Sample(), "0", filter: "loc", cancellationToken: _ct);

        // Containers are listed too, with their children after them.
        Assert.Equal(["Location", "Location.X", "Location.Y"], result.Properties.Select(p => p.Path));
        Assert.Equal(3, result.Total);
    }

    [Fact]
    public async Task DumpExport_UnderAPath_ListsOnlyThatSubtree()
    {
        var result = await _rig.Assets.DumpExportAsync(Sample(), "0", path: "Tags", cancellationToken: _ct);

        Assert.Equal(["Tags", "Tags[0]", "Tags[1]"], result.Properties.Select(p => p.Path));
    }

    [Fact]
    public async Task DumpExport_CapsTheResultAndSaysSo()
    {
        var path = Sample(asset => TestAssets.AddStructArray(asset, (NormalExport)asset.Exports[0], "Items", [.. Enumerable.Range(1, 50)]));

        var result = await _rig.Assets.DumpExportAsync(path, "0", filter: "Items", maxResults: 10, cancellationToken: _ct);

        Assert.Equal(10, result.Properties.Count);
        Assert.Equal(101, result.Total); // the array, its 50 elements and their 50 values
        Assert.Equal("Items", result.Properties[0].Path);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task DumpExport_RefusesAMaxResultsOutsideTheRange()
    {
        var path = Sample();

        await Assert.ThrowsAsync<McpException>(() => _rig.Assets.DumpExportAsync(path, "0", maxResults: 0, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.Assets.DumpExportAsync(path, "0", maxResults: 2001, cancellationToken: _ct));
    }

    [Fact]
    public async Task DumpExport_WithAnExportThatDoesNotExist_ReportsIt()
    {
        var path = Sample();

        var error = await Assert.ThrowsAsync<ArgException>(() => _rig.Assets.DumpExportAsync(path, "Nope", cancellationToken: _ct));

        Assert.Contains("No export name contains 'Nope'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DumpExport_PutsAValueOnOneLine_WhateverItHolds()
    {
        var path = Sample(asset => ((StrPropertyData)((NormalExport)asset.Exports[0]).Data.Single(p => p.Name.ToString() == "DisplayName")).Value =
            FString.FromString("line one\nline two\u001b[31m red"));

        var result = await _rig.Assets.DumpExportAsync(path, "0", filter: "DisplayName", cancellationToken: _ct);

        var value = Assert.Single(result.Properties).Value;
        Assert.DoesNotContain('\n', value);
        Assert.DoesNotContain('\u001b', value);
        Assert.StartsWith("line one line two", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DumpExport_CutsAVeryLongValue()
    {
        var path = Sample(asset => ((StrPropertyData)((NormalExport)asset.Exports[0]).Data.Single(p => p.Name.ToString() == "DisplayName")).Value =
            FString.FromString(new string('x', 5000)));

        var result = await _rig.Assets.DumpExportAsync(path, "0", filter: "DisplayName", cancellationToken: _ct);

        var value = Assert.Single(result.Properties).Value;
        Assert.Equal(503, value.Length);
        Assert.EndsWith("...", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DumpExport_WhenCancelled_Throws()
    {
        var path = Sample();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Assets.DumpExportAsync(path, "0", cancellationToken: McpRig.Cancelled));
    }

    [Fact]
    public async Task SearchAssets_InOneAsset_FindsAPropertyByValue()
    {
        var path = Sample();

        var result = await _rig.Assets.SearchAssetsAsync(path, value: "hello", cancellationToken: _ct);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("DisplayName", hit.Path);
        Assert.Equal("Hello World", hit.Value);
        Assert.Equal("TestExport", hit.ExportName);
        Assert.Equal("Property", hit.Kind);
        Assert.Equal(path, hit.Asset);
    }

    [Fact]
    public async Task SearchAssets_ByReference_FindsTheImport()
    {
        var result = await _rig.Assets.SearchAssetsAsync(Sample(), reference: "T_Wall.T_Wall", cancellationToken: _ct);

        var hit = Assert.Single(result.Hits);
        Assert.Equal("Reference", hit.Kind);
        Assert.Equal("/Game/Textures/T_Wall.T_Wall", hit.Value);
    }

    [Fact]
    public async Task SearchAssets_InAFolder_SearchesRecursively_InPathOrder_AndReportsProgress()
    {
        var second = Sample(name: "Sub/B.uasset");
        var first = Sample(name: "A.uasset");
        var progress = new CollectedProgress();

        var result = await _rig.Assets.SearchAssetsAsync(_rig.Folder.Path, value: "Hello", progress: progress, cancellationToken: _ct);

        Assert.Equal(2, result.Total);
        Assert.Equal([Normalise(first), Normalise(second)], result.Hits.Select(h => Normalise(Path.Combine(_rig.Folder.Path, h.Asset))));
        Assert.Contains(progress.Values, p => p.Progress == 2 && p.Total == 2);
    }

    private static string Normalise(string path) => Path.GetFullPath(path).Replace('\\', '/');

    [Fact]
    public async Task SearchAssets_CapsTheHitsAndSaysSo()
    {
        Sample(name: "A.uasset");
        Sample(name: "B.uasset");

        var result = await _rig.Assets.SearchAssetsAsync(_rig.Folder.Path, value: "Hello", maxResults: 1, cancellationToken: _ct);

        Assert.Single(result.Hits);
        Assert.Equal(2, result.Total);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task SearchAssets_WithoutAnyTerm_AsksForOne()
    {
        var error = await Assert.ThrowsAsync<McpException>(() => _rig.Assets.SearchAssetsAsync(Sample(), cancellationToken: _ct));

        Assert.Contains("at least one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAssets_WithRegex_MatchesThePattern_AndRefusesABadOne()
    {
        var path = Sample();

        var result = await _rig.Assets.SearchAssetsAsync(path, propertyName: "^Location\\.[XY]$", regex: true, cancellationToken: _ct);
        var bad = await Assert.ThrowsAsync<McpException>(() => _rig.Assets.SearchAssetsAsync(path, value: "(unclosed", regex: true, cancellationToken: _ct));

        Assert.Equal(["Location.X", "Location.Y"], result.Hits.Select(h => h.Path));
        Assert.Contains("regular expression", bad.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAssets_WhenCancelled_ThrowsForAFolderAndForAFile()
    {
        var path = Sample();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Assets.SearchAssetsAsync(_rig.Folder.Path, value: "Hello", cancellationToken: McpRig.Cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Assets.SearchAssetsAsync(path, value: "Hello", cancellationToken: McpRig.Cancelled));
    }

    [Fact]
    public async Task SearchAssets_WithAMissingPath_SaysSo()
    {
        var error = await Assert.ThrowsAsync<McpException>(() => _rig.Assets.SearchAssetsAsync(_rig.Folder.Combine("Nowhere"), value: "x", cancellationToken: _ct));

        Assert.Contains("not found", error.Message, StringComparison.Ordinal);
    }
}
