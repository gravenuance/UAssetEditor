using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UAssetAPI.PropertyTypes.Objects;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.App.ViewModels;

public enum TreeNodeKind
{
    Folder,
    Asset,
    ExportsGroup,
    Export,

    /// <summary>A struct field, array element, or map entry - itself expandable if it's a struct/array/map with something in it.</summary>
    Property,

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

    /// <summary>For an <see cref="TreeNodeKind.Export"/> or <see cref="TreeNodeKind.Property"/> node, the export's index within the asset - inherited unchanged by every Property node descending from a given Export node.</summary>
    public int ExportIndex { get; private init; }

    /// <summary>For a <see cref="TreeNodeKind.ExportsGroup"/>, <see cref="TreeNodeKind.Export"/>, or <see cref="TreeNodeKind.Property"/> node, the owning asset's tree path (its ancestor <see cref="TreeNodeKind.Asset"/> node's <see cref="FullPath"/>) - needed to re-fetch the already-open, already-parsed asset when lazily loading children.</summary>
    public string? AssetPath { get; private init; }

    /// <summary>For a <see cref="TreeNodeKind.Property"/> node, the property it represents - used to lazily fetch its own nested properties (struct fields, array elements, map entries), if any, the first time it's expanded.</summary>
    internal PropertyData? Property { get; private init; }

    /// <summary>For a <see cref="TreeNodeKind.Property"/> node, its full path from the export's root (e.g. "Location", "Row1.Damage", "Scores[Alice]") - the same scheme <c>PropertyWalker</c>'s flat walk uses, so double-clicking this node can open exactly its own subtree into the edit grid.</summary>
    public string? PropertyPath { get; private init; }

    public bool ExportsLoaded { get; private set; }

    /// <summary>For an <see cref="TreeNodeKind.Export"/> or <see cref="TreeNodeKind.Property"/> node - whether its own property children have been loaded yet.</summary>
    public bool PropertiesLoaded { get; private set; }

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

    /// <summary>Replaces the dummy placeholder with one real node per export, once (re-expanding doesn't reload). Every export node gets its own dummy placeholder in turn, so its top-level properties are likewise only loaded once the user expands that particular export.</summary>
    public void MarkExportsLoaded(IReadOnlyList<string> exportNames)
    {
        if (ExportsLoaded) return;
        ExportsLoaded = true;

        Children.Clear();
        for (var i = 0; i < exportNames.Count; i++)
        {
            var exportNode = new AssetTreeItemViewModel(exportNames[i], AssetPath, TreeNodeKind.Export) { ExportIndex = i, AssetPath = AssetPath };
            exportNode.Children.Add(LoadingPlaceholder);
            Children.Add(exportNode);
        }
    }

    /// <summary>
    /// Replaces the dummy placeholder with one real node per struct/array/map property
    /// nested directly under this one (an export's own top-level properties, for an Export
    /// node; one level further in, for a Property node), once. <see cref="PropertyTreeItem"/>
    /// is already filtered to only such properties - a plain scalar (an int, a string, ...)
    /// never becomes its own tree entry, so every node this produces gets its own dummy
    /// placeholder in turn and is itself expandable. Reaching a leaf's actual value means
    /// double-clicking the table that directly contains it open into the edit grid instead.
    /// </summary>
    public void MarkPropertiesLoaded(IReadOnlyList<PropertyTreeItem> items)
    {
        if (PropertiesLoaded) return;
        PropertiesLoaded = true;

        Children.Clear();
        foreach (var item in items)
        {
            var node = new AssetTreeItemViewModel(item.DisplayName, FullPath, TreeNodeKind.Property)
            {
                AssetPath = AssetPath,
                ExportIndex = ExportIndex,
                Property = item.Property,
                PropertyPath = item.Path,
            };
            node.Children.Add(LoadingPlaceholder);
            Children.Add(node);
        }
    }
}
