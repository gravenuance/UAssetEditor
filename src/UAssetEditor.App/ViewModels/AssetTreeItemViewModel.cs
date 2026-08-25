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

    /// <summary>
    /// One chunk listed from a raw, not-yet-converted IoStore container (see
    /// <see cref="MainViewModel.LoadIoStoreAsync"/>) - still Zen-format bytes, not directly
    /// openable/parseable by anything in this app (that's what conversion is for), but
    /// checkable so the user can select entries for <c>ConvertSelectedCommand</c> the same
    /// way <see cref="Folder"/>/<see cref="Asset"/> nodes are checkable for extraction.
    /// </summary>
    ZenAsset,
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
    /// The checkbox means one of two things depending on node kind, never both at once for
    /// the same node - there's no ambiguity in practice since a node is either "loadable
    /// into the results grid" or "extractable to disk," never a candidate for both:
    /// <list type="bullet">
    /// <item>Export/Property nodes: included in <c>LoadSelectedCommand</c>'s multi-selection.
    /// Export nodes are always checkable - by the time one exists, its asset has already
    /// been parsed (expanding "Exports" is what loads them), so checking it can never
    /// trigger a surprise parse. A Property node is checkable only if it has editable
    /// content somewhere in its own subtree (see <see cref="PropertyTreeItem.HasEditableContent"/>) -
    /// a table that only serves to hold other (in turn empty) tables has nothing of its
    /// own worth loading alongside other checked entries.</item>
    /// <item>Folder/Asset nodes: included in <c>ExtractSelectedCommand</c>'s multi-selection -
    /// extracting a whole file or subtree to disk doesn't require having looked at its
    /// contents first the way loading properties into the edit grid does, so these are
    /// always checkable.</item>
    /// </list>
    /// </summary>
    public bool IsCheckable { get; private init; }

    /// <summary>Checked via the tree's checkboxes to build up a multi-item selection for <c>LoadSelectedCommand</c> (Export/Property nodes) or <c>ExtractSelectedCommand</c> (Folder/Asset nodes), independent of the TreeView's own single-item selection highlight.</summary>
    [ObservableProperty] private bool _isChecked;

    private AssetTreeItemViewModel(string name, string? fullPath, TreeNodeKind kind)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
    }

    /// <param name="asZenEntries">
    /// True for a raw, not-yet-converted IoStore container's tree (see
    /// <see cref="MainViewModel.LoadIoStoreAsync"/>): every leaf becomes a checkable
    /// <see cref="TreeNodeKind.ZenAsset"/> instead of the usual Asset/OtherFile split, since
    /// none of them are openable/parseable pre-conversion regardless of name.
    /// </param>
    public AssetTreeItemViewModel(PathTreeNode node, bool asZenEntries = false)
    {
        ArgumentNullException.ThrowIfNull(node);

        Name = node.Name;
        FullPath = node.FullPath;

        if (!node.IsLeaf)
        {
            Kind = TreeNodeKind.Folder;
            IsCheckable = true;
            foreach (var child in node.Children)
                Children.Add(new AssetTreeItemViewModel(child, asZenEntries));
            return;
        }

        if (asZenEntries)
        {
            Kind = TreeNodeKind.ZenAsset;
            IsCheckable = true;
        }
        else if (node.FullPath != null && node.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
        {
            Kind = TreeNodeKind.Asset;
            IsCheckable = true;
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
        ArgumentNullException.ThrowIfNull(exportNames);

        if (ExportsLoaded) return;
        ExportsLoaded = true;

        Children.Clear();
        for (var i = 0; i < exportNames.Count; i++)
        {
            var exportNode = new AssetTreeItemViewModel(exportNames[i], AssetPath, TreeNodeKind.Export) { ExportIndex = i, AssetPath = AssetPath, IsCheckable = true };
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
        ArgumentNullException.ThrowIfNull(items);

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
                IsCheckable = item.HasEditableContent,
            };
            node.Children.Add(LoadingPlaceholder);
            Children.Add(node);
        }
    }
}
