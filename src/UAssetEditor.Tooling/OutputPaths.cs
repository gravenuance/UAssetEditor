using UAssetEditor.Core;

namespace UAssetEditor.Tooling;

/// <summary>Guards on where an operation may write, shared by every front end. Each path is resolved first (<see cref="FinalPaths"/>),
/// so a link, junction, 8.3 name or other spelling does not get round them.</summary>
internal static class OutputPaths
{
    /// <summary>Refuses <paramref name="candidate"/> when it lies inside <paramref name="folder"/>, so an output cannot end up in what it is built from.</summary>
    public static void RefuseInside(string candidate, string folder, string candidateName, string folderName)
    {
        if (Placed(() => FinalPaths.IsInside(candidate, folder)))
            throw new ArgException($"{candidateName} must not be inside {folderName}.");
    }

    /// <summary>Refuses an output written in place (not staged and moved over it) when that write would change a file in
    /// <paramref name="folder"/>: it lies inside, or is a hard link to a file there.</summary>
    public static void RefuseWriteInto(string candidate, string folder, string candidateName, string folderName)
    {
        if (Placed(() => FinalPaths.ReachesInto(candidate, folder)))
            throw new ArgException($"{candidateName} must not be inside {folderName} or a hard link to a file in it.");
    }

    /// <summary>Refuses two paths that name the same file, however they are spelled, hard links included.</summary>
    public static void RefuseSame(string first, string second, string firstName, string secondName)
    {
        if (Placed(() => FinalPaths.SameFile(first, second))) throw new ArgException($"{firstName} and {secondName} are the same file.");
    }

    /// <summary>Refuses an existing file at <paramref name="path"/> unless <paramref name="overwrite"/>; <paramref name="overwriteOption"/> names how to allow it.</summary>
    public static void RefuseExisting(string path, string name, bool overwrite, string overwriteOption)
    {
        if (!overwrite && File.Exists(path))
            throw new ArgException($"{name} already exists: {path}. Pass {overwriteOption} to replace it, or choose another path.");
    }

    /// <summary><see cref="FinalPaths.Resolve"/>, with a failure to resolve reported as bad input.</summary>
    public static string Resolve(string path) => Placed(() => FinalPaths.Resolve(path));

    // An unplaceable path keeps its own exception, which the front ends report as it is; any other I/O failure of the resolution (a path too long) is bad input.
    private static T Placed<T>(Func<T> resolve)
    {
        try
        {
            return resolve();
        }
        catch (IOException ex) when (ex is not PathNotPlaceableException)
        {
            throw new ArgException(ex.Message, ex);
        }
    }
}
