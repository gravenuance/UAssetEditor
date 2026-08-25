using CommunityToolkit.Mvvm.ComponentModel;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Search;

namespace UAssetEditor.App.ViewModels;

/// <summary>
/// Wraps a <see cref="SearchResult"/> with a mutable, directly-editable <see cref="Value"/>.
/// Editing it writes straight into the matching property on the workspace's already-open
/// in-memory <see cref="UAssetAPI.UAsset"/> - no reselecting a node, no re-running search,
/// and any number of rows across any number of assets can be edited this way before an
/// explicit save.
/// </summary>
public partial class SearchResultRow : ObservableObject
{
    private readonly AssetWorkspace _workspace;
    private readonly Action<SearchResultRow> _onDirty;

    public SearchResult Source { get; }

    public string AssetPath => Source.AssetPath;
    public int ExportIndex => Source.ExportIndex;
    public string ExportName => Source.ExportName;
    public string? PropertyPath => Source.PropertyPath;
    public SearchMatchKind Kind => Source.Kind;

    [ObservableProperty] private string _value;
    [ObservableProperty] private bool _isDirty;

    public SearchResultRow(SearchResult source, AssetWorkspace workspace, Action<SearchResultRow> onDirty)
    {
        ArgumentNullException.ThrowIfNull(source);

        Source = source;
        _workspace = workspace;
        _onDirty = onDirty;
        _value = source.MatchedText; // bypasses the setter below - this is the initial load, not an edit
    }

    partial void OnValueChanged(string value)
    {
        if (Kind != SearchMatchKind.Property || PropertyPath == null) return;

        var asset = _workspace.GetOrOpen(AssetPath);
        var node = PropertyLocator.Locate(asset, ExportIndex, PropertyPath);
        if (node == null) return;

        if (!PropertyValueAccessor.TrySetStringValue(node.Property, value, asset))
        {
            // Invalid input for this property's type (e.g. non-numeric text typed into an
            // int property): the underlying property was left untouched, so revert the
            // displayed text to match rather than silently leaving the grid showing an
            // edit that was never actually applied. Written directly to the backing field
            // (bypassing the generated setter) so this doesn't re-enter OnValueChanged and
            // flag the row dirty for a no-op "edit".
            _value = PropertyValueAccessor.AsSearchableString(node.Property, asset) ?? _value;
            OnPropertyChanged(nameof(Value));
            return;
        }

        PropertyValueAccessor.UpdateIsZeroFlag(node.Property);

        IsDirty = true;
        _onDirty(this);
    }
}
