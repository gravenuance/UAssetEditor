using System.Linq;
using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.App.Tests;

/// <summary>
/// Covers the "compact folders" behavior added to <see cref="AssetTreeItemViewModel"/>'s
/// <see cref="PathTreeNode"/> constructor - a real pak's mount-point-relative prefix
/// (e.g. "../../../Marvel/Content/Marvel/") is a run of folders with exactly one child apiece,
/// which used to force several expand clicks that revealed nothing before reaching anything
/// actually browsable (confirmed from a real screenshot: three ".."-named rows, each too
/// visually thin at the tree's caption font size to read as a real label at all).
/// </summary>
public class AssetTreeItemViewModelTests
{
    // Mirrors MainViewModel.RebuildTreeAsync exactly: the synthetic ""-named root PathTreeBuilder
    // returns is never itself wrapped - only its children are, individually.
    private static List<AssetTreeItemViewModel> BuildRootItems(params string[] paths) =>
        PathTreeBuilder.Build(paths).Children.Select(child => new AssetTreeItemViewModel(child)).ToList();

    [Fact]
    public void SingleChildFolderChain_CompactsIntoOneRow()
    {
        var rootItems = BuildRootItems(
            "../../../Marvel/Content/Marvel/Characters/1011/Foo.uasset",
            "../../../Marvel/Content/Marvel/Characters/1014/Bar.uasset");

        // ".." -> ".." -> ".." -> "Marvel" -> "Content" -> "Marvel" -> "Characters" each have
        // exactly one child at the point they're reached, so all seven collapse into this one
        // row (matching VS Code's own compact-folders behavior: a chain's *last* folder is
        // included in the merged label regardless of how many children it itself has - what
        // decides the merge is that each of its *ancestors* had exactly one). Expanding it
        // reveals Characters' own two real children (1011, 1014) directly.
        var compacted = Assert.Single(rootItems);
        Assert.Equal("../../../Marvel/Content/Marvel/Characters", compacted.Name);
        Assert.Equal(TreeNodeKind.Folder, compacted.Kind);
        Assert.True(compacted.IsCheckable);
        Assert.Equal(2, compacted.Children.Count);
        Assert.Contains(compacted.Children, c => c.Name == "1011");
        Assert.Contains(compacted.Children, c => c.Name == "1014");
    }

    [Fact]
    public void FolderWithMultipleChildren_IsNotCompacted()
    {
        var rootItems = BuildRootItems("Content/Foo.uasset", "Content/Bar.uasset");

        var content = Assert.Single(rootItems);
        Assert.Equal("Content", content.Name);
        Assert.Equal(2, content.Children.Count);
    }

    [Fact]
    public void FolderWhoseSingleChildIsAFile_IsNotCompacted()
    {
        // A folder that bottoms out at exactly one *file* stays its own row - compacting a
        // folder together with a file it contains (as opposed to another folder) wouldn't
        // make sense, since the file isn't itself expandable the way a folder is.
        var rootItems = BuildRootItems("Content/Foo.uasset");

        var content = Assert.Single(rootItems);
        Assert.Equal("Content", content.Name);
        var file = Assert.Single(content.Children);
        Assert.Equal("Foo.uasset", file.Name);
        Assert.Equal(TreeNodeKind.Asset, file.Kind);
    }

    [Fact]
    public void CompactedRow_FullPathIsTheDeepestMergedNodes()
    {
        // The compacted row must still carry one real, checkable path - the deepest node's -
        // so checking it for ExtractSelectedCommand/RepackSelectedCommand scopes to exactly
        // what every intermediate level would have scoped to on its own.
        var rootItems = BuildRootItems("A/B/C/Foo.uasset", "A/B/C/Bar.uasset");

        var compacted = Assert.Single(rootItems);
        Assert.Equal("A/B/C", compacted.Name);
        Assert.Equal("A/B/C", compacted.FullPath);
    }

    [Fact]
    public void ResetLoadedState_CollapsesExportsBackToNeverExpanded()
    {
        // Regression test: RevertEditsCommand discards and re-parses the underlying UAsset,
        // but previously left an already-expanded asset's tree nodes showing whatever
        // structure (e.g. a duplicated array element) existed before the revert - editing or
        // duplicating one of those now-nonexistent rows afterward silently did nothing, since
        // PropertyLocator has nothing matching to find against the freshly re-parsed asset.
        var rootItems = BuildRootItems("Content/Foo.uasset");
        var asset = Assert.Single(Assert.Single(rootItems).Children);
        var exportsGroup = Assert.Single(asset.Children, c => c.Kind == TreeNodeKind.ExportsGroup);

        exportsGroup.MarkExportsLoaded(["Export1", "Export2"]);
        exportsGroup.IsExpanded = true;

        asset.ResetLoadedState();

        Assert.False(exportsGroup.ExportsLoaded);
        Assert.False(exportsGroup.IsExpanded);
        var placeholder = Assert.Single(exportsGroup.Children);
        Assert.Equal("Loading...", placeholder.Name);
    }

    [Fact]
    public void ResetLoadedState_OnAnUnexpandedAsset_IsANoOp()
    {
        var rootItems = BuildRootItems("Content/Foo.uasset");
        var asset = Assert.Single(Assert.Single(rootItems).Children);
        var exportsGroup = Assert.Single(asset.Children, c => c.Kind == TreeNodeKind.ExportsGroup);

        asset.ResetLoadedState();

        Assert.False(exportsGroup.ExportsLoaded);
        Assert.Single(exportsGroup.Children);
    }
}
