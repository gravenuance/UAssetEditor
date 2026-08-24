using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

public class PathTreeBuilderTests
{
    [Fact]
    public void Build_NestsPathsByDirectorySegment()
    {
        var root = PathTreeBuilder.Build(["Content/Foo/Bar.uasset", "Content/Foo/Baz.uasset", "Content/Other.uasset"]);

        var content = Assert.Single(root.Children);
        Assert.Equal("Content", content.Name);
        Assert.False(content.IsLeaf);
        // Folder nodes carry their own accumulated path too, not just leaves - needed to
        // scope an extraction to a checked folder's subtree (see AssetTreeItemViewModel).
        Assert.Equal("Content", content.FullPath);

        Assert.Equal(2, content.Children.Count);
        var foo = Assert.Single(content.Children, c => c.Name == "Foo");
        Assert.False(foo.IsLeaf);
        Assert.Equal(2, foo.Children.Count);

        var bar = Assert.Single(foo.Children, c => c.Name == "Bar.uasset");
        Assert.True(bar.IsLeaf);
        Assert.Equal("Content/Foo/Bar.uasset", bar.FullPath);

        var other = Assert.Single(content.Children, c => c.Name == "Other.uasset");
        Assert.True(other.IsLeaf);
        Assert.Equal("Content/Other.uasset", other.FullPath);
    }

    [Fact]
    public void Build_HandlesFilesWithNoDirectoryPrefix()
    {
        var root = PathTreeBuilder.Build(["TopLevel.uasset"]);

        var top = Assert.Single(root.Children);
        Assert.Equal("TopLevel.uasset", top.Name);
        Assert.True(top.IsLeaf);
        Assert.Equal("TopLevel.uasset", top.FullPath);
    }

    [Fact]
    public void Build_KeepsSameNamedEntriesAtDifferentLevelsDistinct()
    {
        var root = PathTreeBuilder.Build(["A/Shared.uasset", "B/Shared.uasset"]);

        var aShared = root.Children.Single(c => c.Name == "A").Children.Single();
        var bShared = root.Children.Single(c => c.Name == "B").Children.Single();

        Assert.Equal("A/Shared.uasset", aShared.FullPath);
        Assert.Equal("B/Shared.uasset", bShared.FullPath);
    }

    [Fact]
    public void Build_DeduplicatesRepeatedPaths()
    {
        var root = PathTreeBuilder.Build(["Content/Foo.uasset", "Content/Foo.uasset"]);

        var content = Assert.Single(root.Children);
        Assert.Single(content.Children);
    }
}
