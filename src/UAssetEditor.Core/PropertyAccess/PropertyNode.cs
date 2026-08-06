using UAssetAPI.PropertyTypes.Objects;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// A single property found while walking an export's property tree, along with enough
/// context (its owning list/array and index) to mutate or remove it in place.
/// </summary>
public sealed record PropertyNode(string Path, PropertyData Property, IList<PropertyData>? Owner, int OwnerIndex);
