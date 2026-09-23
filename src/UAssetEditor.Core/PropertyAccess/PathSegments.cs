namespace UAssetEditor.Core.PropertyAccess;

/// <summary>One step of a property path: a field name, or a bracketed array index (<see cref="Index"/> is null for a non-numeric map key).</summary>
internal readonly record struct PathSegment(string? Name, int? Index);

/// <summary>Splits the "Foo.Bar[2].Baz" paths <see cref="PropertyPaths"/> builds back into steps.</summary>
internal static class PathSegments
{
    public static bool TryParse(string path, out List<PathSegment> segments)
    {
        segments = [];
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '[')
            {
                var close = path.IndexOf(']', i + 1);
                if (close < 0) return false;
                var key = path.AsSpan(i + 1, close - i - 1);
                segments.Add(new PathSegment(null, int.TryParse(key, out var index) ? index : null));
                i = close + 1;
                if (i < path.Length && path[i] == '.') i++;
                continue;
            }

            var end = path.IndexOfAny(['.', '['], i);
            if (end < 0) end = path.Length;
            if (end == i) return false;
            segments.Add(new PathSegment(path[i..end], null));
            i = end < path.Length && path[end] == '.' ? end + 1 : end;
        }

        return segments.Count > 0;
    }
}
