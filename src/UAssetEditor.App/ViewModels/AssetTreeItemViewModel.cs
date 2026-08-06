using System.Collections.ObjectModel;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.App.ViewModels;

/// <summary>A thin, eagerly-materialized wrapper over a <see cref="PathTreeNode"/> for TreeView binding.</summary>
public sealed class AssetTreeItemViewModel
{
    public string Name { get; }
    public string? FullPath { get; }
    public bool IsLeaf { get; }
    public ObservableCollection<AssetTreeItemViewModel> Children { get; }

    public AssetTreeItemViewModel(PathTreeNode node)
    {
        Name = node.Name;
        FullPath = node.FullPath;
        IsLeaf = node.IsLeaf;
        Children = new ObservableCollection<AssetTreeItemViewModel>(node.Children.Select(c => new AssetTreeItemViewModel(c)));
    }
}
