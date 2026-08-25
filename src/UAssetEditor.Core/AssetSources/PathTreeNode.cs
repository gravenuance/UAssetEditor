using System.Collections.ObjectModel;

namespace UAssetEditor.Core.AssetSources;

/// <summary>One node in a browsable tree built from a flat list of separator-delimited paths.</summary>
public sealed class PathTreeNode(string name, string? fullPath, bool isLeaf)
{
    public string Name { get; } = name;

    /// <summary>The original full path this node represents, for a leaf; null for a pure folder node.</summary>
    public string? FullPath { get; } = fullPath;

    public bool IsLeaf { get; } = isLeaf;

    public Collection<PathTreeNode> Children { get; } = new();
}
