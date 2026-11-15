namespace UAssetEditor.Core;

/// <summary>A path with a link in it that the file system cannot place (a link loop, an offline share, a link that cannot
/// be read), so <see cref="FinalPaths"/> refuses it rather than judge it by its name. The message names the path.</summary>
public sealed class PathNotPlaceableException : IOException
{
    private const string LinkLoopText = "Too many levels of links resolving";

    public PathNotPlaceableException()
    {
    }

    public PathNotPlaceableException(string message)
        : base(message)
    {
    }

    public PathNotPlaceableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    internal static PathNotPlaceableException LinkLoop(string path) => new($"{LinkLoopText} {path}.");

    internal static PathNotPlaceableException Unplaceable(string path, string reason, Exception? inner = null) =>
        new($"Cannot tell where {path} lands, so it is refused: {reason}", inner);
}
