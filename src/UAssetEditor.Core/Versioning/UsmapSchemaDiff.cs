using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.Versioning;

/// <summary>A schema whose own property layout differs between two usmaps.</summary>
/// <param name="Added">"Name=Type" of each property only the new schema has.</param>
/// <param name="Removed">"Name=Type" of each property only the old schema has.</param>
public sealed record SchemaChange(
    string Name, int OldCount, int NewCount, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, string? OldSuper, string? NewSuper)
{
    /// <summary>Same properties and parent, at different positions or array sizes; still shifts unversioned data.</summary>
    public bool LayoutOnly => Added.Count == 0 && Removed.Count == 0 && OldSuper == NewSuper;
}

/// <param name="Changed">Schemas in both usmaps with a different layout, by name.</param>
/// <param name="Added">Schemas only the new usmap has, by name.</param>
/// <param name="Removed">Schemas only the old usmap has, by name.</param>
public sealed record UsmapDiff(IReadOnlyList<SchemaChange> Changed, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

/// <summary>
/// Compares two usmaps schema by schema. Unversioned properties are read by schema position, so any changed schema
/// means an asset built against the old usmap is misread under the new one if it serialises that class or struct.
/// </summary>
public static class UsmapSchemaDiff
{
    public static UsmapDiff Compare(Usmap oldMappings, Usmap newMappings)
    {
        ArgumentNullException.ThrowIfNull(oldMappings);
        ArgumentNullException.ThrowIfNull(newMappings);

        var changed = new List<SchemaChange>();
        var removed = new List<string>();
        foreach (var (name, before) in oldMappings.Schemas.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!newMappings.Schemas.TryGetValue(name, out var after))
            {
                removed.Add(name);
                continue;
            }
            if (Layout(before) == Layout(after)) continue;

            var beforeProperties = Fields(before);
            var afterProperties = Fields(after);
            changed.Add(new SchemaChange(
                name, before.PropCount, after.PropCount,
                [.. afterProperties.Except(beforeProperties)], [.. beforeProperties.Except(afterProperties)],
                before.SuperType, after.SuperType));
        }

        var added = newMappings.Schemas.Keys.Where(name => !oldMappings.Schemas.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
        return new UsmapDiff(changed, added, removed);
    }

    // Everything that decides where an unversioned value lands: parent, count, and each property's index, name, size and type.
    private static string Layout(UsmapSchema schema) =>
        $"super={schema.SuperType} count={schema.PropCount} | " +
        string.Join("; ", schema.Properties.OrderBy(entry => entry.Key).Select(entry =>
            $"{entry.Key}:{entry.Value.Name}[{entry.Value.ArraySize}]={entry.Value.PropertyData}"));

    private static List<string> Fields(UsmapSchema schema) =>
        [.. schema.Properties.OrderBy(entry => entry.Key).Select(entry => $"{entry.Value.Name}={entry.Value.PropertyData}")];
}
