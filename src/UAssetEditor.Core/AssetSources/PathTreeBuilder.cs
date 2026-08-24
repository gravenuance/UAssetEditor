namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Turns a flat list of paths (a directory walk, or a pak's file index) into a nested
/// <see cref="PathTreeNode"/> tree. Both loose folders and paks already expose their
/// complete path list cheaply (a directory walk or the pak's index respectively), so the
/// whole tree is built eagerly here - no per-node lazy I/O is needed on the UI side.
/// </summary>
public static class PathTreeBuilder
{
    public static PathTreeNode Build(IEnumerable<string> paths, char separator = '/')
    {
        var root = new PathTreeNode("", null, false);
        var lookup = new Dictionary<string, PathTreeNode>(StringComparer.Ordinal) { [""] = root };

        foreach (var path in paths.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var segments = path.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;

            var current = root;
            var accumulated = "";

            for (var i = 0; i < segments.Length; i++)
            {
                accumulated = accumulated.Length == 0 ? segments[i] : $"{accumulated}{separator}{segments[i]}";
                var isLeaf = i == segments.Length - 1;

                if (!lookup.TryGetValue(accumulated, out var node))
                {
                    // FullPath is set for every node, not just leaves, now that folders need
                    // their own accumulated path too (see AssetTreeItemViewModel.IsCheckable -
                    // Folder nodes are checkable for extraction, which needs a real path to
                    // scope the entry filter to).
                    node = new PathTreeNode(segments[i], accumulated, isLeaf);
                    lookup[accumulated] = node;
                    current.Children.Add(node);
                }

                current = node;
            }
        }

        return root;
    }
}
