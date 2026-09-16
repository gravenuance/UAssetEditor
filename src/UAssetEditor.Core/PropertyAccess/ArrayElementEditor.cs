using UAssetAPI.PropertyTypes.Objects;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Adds and removes elements of an array property in place. An array's backing store
/// (<see cref="ArrayPropertyData.Value"/>) is a fixed-size PropertyData[], not a resizable
/// List{T}, so every mutation here replaces the whole array rather than trying
/// IList{T}.Add/RemoveAt on it (which throws NotSupportedException on a fixed-size array -
/// see Editing.EditExecutor's RemovePropertyRule, which only accepts a List{PropertyData}
/// owner for exactly this reason and can't touch an array element at all).
/// </summary>
public static class ArrayElementEditor
{
    /// <summary>Deep-clones the element at <paramref name="index"/> (via UAssetAPI's own <see cref="System.ICloneable"/> support on every PropertyData) and appends the clone as the array's new last element.</summary>
    public static PropertyData Duplicate(ArrayPropertyData array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        var elements = array.Value ?? [];
        if (index < 0 || index >= elements.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        var clone = (PropertyData)elements[index].Clone();
        array.Value = [.. elements, clone];
        return clone;
    }

    /// <summary>
    /// Deep-clones <paramref name="template"/> (which need not belong to <paramref name="array"/> -
    /// e.g. cloning a sibling struct property of the same type) and appends the clone as the
    /// array's new last element. The counterpart to <see cref="Duplicate"/> for an array with
    /// no existing element of its own to clone from (an empty ExcludeBones list, for instance).
    /// </summary>
    public static PropertyData AppendClone(ArrayPropertyData array, PropertyData template)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentNullException.ThrowIfNull(template);

        var clone = (PropertyData)template.Clone();
        array.Value = [.. (array.Value ?? []), clone];
        return clone;
    }

    /// <summary>Removes the element at <paramref name="index"/>, shifting every later element down by one.</summary>
    public static void RemoveAt(ArrayPropertyData array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        var elements = array.Value ?? [];
        if (index < 0 || index >= elements.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        array.Value = elements.Where((_, i) => i != index).ToArray();
    }
}
