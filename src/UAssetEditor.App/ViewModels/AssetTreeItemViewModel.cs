using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.App.ViewModels;

public enum TreeNodeKind
{
    Folder,
    Asset,
    ExportsGroup,
    Export,

    /// <summary>A non-.uasset leaf file (.uexp, .ubulk, etc.) - shown, but not openable.</summary>
    OtherFile,
}

/// <summary>
/// A browsable tree node. Built eagerly from a <see cref="PathTreeNode"/> for folder/file
/// structure, but a <see cref="TreeNodeKind.Asset"/> node's own exports are never known
/// (and never worth parsing the asset just to find out) until the user actually expands
/// its synthetic "Exports" child - so every .uasset node gets exactly one
/// <see cref="TreeNodeKind.ExportsGroup"/> child with a single dummy placeholder, and real
/// per-export children only appear once <see cref="MarkExportsLoaded"/> is called.
/// </summary>
public sealed partial class AssetTreeItemViewModel : ObservableObject
{
    private static readonly AssetTreeItemViewModel LoadingPlaceholder =
        new("Loading...", null, TreeNodeKind.OtherFile);

    public string Name { get; }
    public string? FullPath { get; }
    public TreeNodeKind Kind { get; }
    public ObservableCollection<AssetTreeItemViewModel> Children { get; } = new();

    /// <summary>For a <see cref="TreeNodeKind.Export"/> node, the export's index within the asset.</summary>
    public int ExportIndex { get; private init; }

    /// <summary>For a <see cref="TreeNodeKind.ExportsGroup"/> node, the owning asset's tree path (its parent <see cref="TreeNodeKind.Asset"/> node's <see cref="FullPath"/>).</summary>
    public string? AssetPath { get; private init; }

    public bool ExportsLoaded { get; private set; }

    /// <summary>
    /// Only Export nodes are checkable - by the time one exists, its asset has already
    /// been parsed (expanding "Exports" is what loads them), so checking it can never
    /// trigger a surprise parse. An Asset node is deliberately not checkable: selecting
    /// its exports means actually looking at what's there first, not blindly grabbing
    /// everything sight-unseen.
    /// </summary>
    public bool IsCheckable => Kind is TreeNodeKind.Export;

    /// <summary>Checked via the tree's checkboxes to build up a multi-item selection for <c>LoadSelectedCommand</c>, independent of the TreeView's own single-item selection highlight.</summary>
    [ObservableProperty] private bool _isChecked;

    private AssetTreeItemViewModel(string name, string? fullPath, TreeNodeKind kind)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
    }

    public AssetTreeItemViewModel(PathTreeNode node)
    {
        Name = node.Name;
        FullPath = node.FullPath;

        if (!node.IsLeaf)
        {
            Kind = TreeNodeKind.Folder;
            foreach (var child in node.Children)
                Children.Add(new AssetTreeItemViewModel(child));
            return;
        }

        if (node.FullPath != null && node.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        {
            Kind = TreeNodeKind.Asset;
            var exportsGroup = new AssetTreeItemViewModel("Exports", null, TreeNodeKind.ExportsGroup) { AssetPath = node.FullPath };
            exportsGroup.Children.Add(LoadingPlaceholder);
            Children.Add(exportsGroup);
        }
        else
        {
            Kind = TreeNodeKind.OtherFile;
        }
    }

    /// <summary>Replaces the dummy placeholder with one real node per export, once (re-expanding doesn't reload).</summary>
    public void MarkExportsLoaded(IReadOnlyList<string> exportNames)
    {
        if (ExportsLoaded) return;
        ExportsLoaded = true;

        Children.Clear();
        for (var i = 0; i < exportNames.Count; i++)
            Children.Add(new AssetTreeItemViewModel(exportNames[i], AssetPath, TreeNodeKind.Export) { ExportIndex = i });
    }
}
