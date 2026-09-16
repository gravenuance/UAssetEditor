using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
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

    /// <summary>
    /// The node whose <see cref="Children"/> this one lives in - null only for a root item
    /// (never set on the shared <see cref="LoadingPlaceholder"/> singleton, since it's reused
    /// under many different parents at once). Lets a structural edit on one Property node
    /// (see <c>MainViewModel.DuplicateArrayElementCommand</c>/<c>RemoveArrayElementCommand</c>)
    /// find its owning array's tree node and refresh that node's children in place.
    /// </summary>
    public AssetTreeItemViewModel? Parent { get; private init; }

    /// <summary>For an <see cref="TreeNodeKind.Export"/> or <see cref="TreeNodeKind.Property"/> node, the export's index within the asset - inherited unchanged by every Property node descending from a given Export node.</summary>
    public int ExportIndex { get; private init; }

    /// <summary>For a <see cref="TreeNodeKind.ExportsGroup"/>, <see cref="TreeNodeKind.Export"/>, or <see cref="TreeNodeKind.Property"/> node, the owning asset's tree path (its ancestor <see cref="TreeNodeKind.Asset"/> node's <see cref="FullPath"/>) - needed to re-fetch the already-open, already-parsed asset when lazily loading children.</summary>
    public string? AssetPath { get; private init; }

    /// <summary>For a <see cref="TreeNodeKind.Property"/> node, the property it represents - used to lazily fetch its own nested properties (struct fields, array elements, map entries), if any, the first time it's expanded.</summary>
    internal PropertyData? Property { get; private init; }

    /// <summary>For a <see cref="TreeNodeKind.Property"/> node, its full path from the export's root (e.g. "Location", "Row1.Damage", "Scores[Alice]") - the same scheme <c>PropertyWalker</c>'s flat walk uses, so double-clicking this node can open exactly its own subtree into the edit grid.</summary>
    public string? PropertyPath { get; private init; }

    /// <summary>For a <see cref="TreeNodeKind.Property"/> node, whether it's one element of an array (as opposed to a struct field or map entry) - the only kind of Property node <c>MainViewModel.DuplicateArrayElementCommand</c>/<c>RemoveArrayElementCommand</c> can act on, since a struct field's owner isn't resizable the same way.</summary>
    public bool IsArrayElement { get; private init; }

    /// <summary>
    /// For a <see cref="TreeNodeKind.Property"/> node, whether it's a struct-typed property
    /// sitting directly on its export's own root (e.g. an "AnimGraphNode_KawaiiPhysics_N" on a
    /// class default object) rather than nested inside another struct/array/map. The only kind
    /// of node <c>MainViewModel.AddAnimGraphNodeCommand</c> can clone as a brand new class
    /// member - see <see cref="Core.PropertyAccess.ClassPropertyDeclarer"/> for why a whole new
    /// class member has to start from a top-level property, not an arbitrarily nested one.
    /// Best-effort: true whenever the shape looks right, even for an export that turns out not
    /// to have a companion class - ClassPropertyDeclarer itself is the real gate, and fails with
    /// a clear message rather than silently doing nothing.
    /// </summary>
    public bool IsTopLevelStructProperty { get; private init; }

    /// <summary>Whether this node has any right-click tree action at all (see MainWindow.xaml's ItemContainerStyle) - the union gating whether the context menu attaches, with each item's own Visibility further narrowing which specific action(s) show.</summary>
    public bool HasTreeContextAction => IsArrayElement || IsTopLevelStructProperty;

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

    /// <summary>
    /// Two-way bound to the TreeView's own container (see MainWindow.xaml's ItemContainerStyle),
    /// so manual expand/collapse clicks and <c>ExpandAllFoldersCommand</c>/<c>CollapseAllTreeCommand</c>
    /// both go through the same state. <c>ExpandAllFoldersCommand</c> only ever sets this true on
    /// <see cref="TreeNodeKind.Folder"/> nodes - setting it on an Asset's "Exports" placeholder or an
    /// Export/Property node would trigger <c>MainWindow.AssetTree_Expanded</c>'s real parse/property
    /// load, which "expand everything" must never do across a tree that can hold hundreds of
    /// thousands of assets (some individually tens of GB).
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

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

            // Compacts a run of folders that each have exactly one non-leaf child into a
            // single row - e.g. a pak's mount-point-relative "../../../ProjectName/Content/
            // ProjectName" prefix, which otherwise forces several expand clicks that reveal
            // nothing (no sibling files, no branching) before reaching anything actually
            // browsable. Matches VS Code's "compact folders" behavior. Still backed by one
            // real path (the deepest merged node's), so checking this row, double-clicking
            // it, or extracting it behaves exactly as if every intermediate level were its
            // own row.
            var names = new List<string> { node.Name };
            var effective = node;
            while (effective.Children.Count == 1 && !effective.Children[0].IsLeaf)
            {
                effective = effective.Children[0];
                names.Add(effective.Name);
            }

            Name = string.Join('/', names);
            FullPath = effective.FullPath;
            foreach (var child in effective.Children)
                Children.Add(new AssetTreeItemViewModel(child, asZenEntries) { Parent = this });
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
            var exportsGroup = new AssetTreeItemViewModel("Exports", null, TreeNodeKind.ExportsGroup) { AssetPath = node.FullPath, Parent = this };
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
            var exportNode = new AssetTreeItemViewModel(exportNames[i], AssetPath, TreeNodeKind.Export) { ExportIndex = i, AssetPath = AssetPath, IsCheckable = true, Parent = this };
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

        RefreshChildren(items);
    }

    /// <summary>
    /// Rebuilds this node's children from scratch to match <paramref name="items"/> - the
    /// same body <see cref="MarkPropertiesLoaded"/> uses for the first-time lazy load, but
    /// callable again afterward to resync after a structural edit
    /// (<c>MainViewModel.DuplicateArrayElementCommand</c>/<c>RemoveArrayElementCommand</c>)
    /// changes how many elements the underlying array holds. A full rebuild rather than an
    /// in-place patch because removing (or inserting before) an element shifts every later
    /// element's positional <see cref="PropertyPath"/> ("Chains[3]" becomes "Chains[2]"), not
    /// just the one that actually changed - so every sibling's identity here is stale, not
    /// only the edited one's.
    /// </summary>
    public void RefreshChildren(IReadOnlyList<PropertyTreeItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Children.Clear();
        foreach (var item in items)
        {
            var node = new AssetTreeItemViewModel(item.DisplayName, FullPath, TreeNodeKind.Property)
            {
                Parent = this,
                AssetPath = AssetPath,
                ExportIndex = ExportIndex,
                Property = item.Property,
                PropertyPath = item.Path,
                IsCheckable = item.HasEditableContent,
                IsArrayElement = item.IsArrayElement,
                IsTopLevelStructProperty = Kind == TreeNodeKind.Export && item.Property is StructPropertyData,
            };
            node.Children.Add(LoadingPlaceholder);
            Children.Add(node);
        }
    }

    /// <summary>
    /// Collapses this Asset node's "Exports" subtree back to its just-constructed, never-expanded
    /// state - every Export/Property node beneath it (each holding a <see cref="Property"/> or
    /// <see cref="ExportIndex"/> captured from whatever <c>UAsset</c> instance was live when it
    /// loaded) is discarded along with it. Needed whenever the workspace discards and re-parses
    /// the underlying asset out from under an already-expanded tree (<c>MainViewModel.RevertEditsCommand</c>) -
    /// otherwise a stale node's cached data (e.g. an array element a since-reverted duplicate
    /// added) keeps showing, and <c>PropertyLocator</c> silently finds nothing for it against the
    /// freshly re-parsed asset, so the next edit/duplicate on that row does nothing at all.
    /// </summary>
    public void ResetLoadedState()
    {
        if (Kind != TreeNodeKind.Asset) return;

        var exportsGroup = Children.FirstOrDefault(c => c.Kind == TreeNodeKind.ExportsGroup);
        if (exportsGroup == null) return;

        exportsGroup.ExportsLoaded = false;
        exportsGroup.IsExpanded = false;
        exportsGroup.Children.Clear();
        exportsGroup.Children.Add(LoadingPlaceholder);
    }
}
