using System.Text.RegularExpressions;

namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>What <see cref="OodleLibrary.Locate"/> found: the DLL to hand the pak worker, or why there is none.</summary>
/// <param name="Path">The full path of a usable <c>oo2core_*_win64.dll</c>, else null.</param>
/// <param name="RejectedEnvironmentValue">What <c>REPAK_OODLE_DLL</c> held when it is set but is not a usable DLL; the lookup then stops there instead of guessing.</param>
public sealed record OodleLookup(string? Path, string? RejectedEnvironmentValue = null);

/// <summary>
/// Finds the user's own Oodle DLL for legacy paks that use it (Days Gone). Oodle is Epic/RAD's proprietary library,
/// so it is never bundled: the order is <c>REPAK_OODLE_DLL</c>, else a copy in the install of the game the pak sits in.
/// Only a regular file named <c>oo2core_&lt;n&gt;_win64.dll</c> is accepted, and neither PATH nor the working folder is searched.
/// </summary>
public static partial class OodleLibrary
{
    public const string EnvironmentVariable = "REPAK_OODLE_DLL";
    public const string FilePattern = "oo2core_*_win64.dll";

    /// <summary>The pak's own folder and this many parents: enough for <c>&lt;root&gt;/&lt;Game&gt;/Content/Paks/~mods</c> and the Rivals layout.</summary>
    internal const int MaxParentLevels = 5;

    /// <summary>How deep under <c>Engine/Binaries/ThirdParty/Oodle</c> to look (Unreal's own layout is <c>Win64/</c>).</summary>
    private const int OodleFolderDepth = 3;

    /// <summary>repak's error text when its Oodle lookup fails; its only cause is a DLL that is missing or will not load.</summary>
    private const string RepakMissingText = "OodleLZ_Decompress was not provided";

    private static readonly string[] OodleFolder = ["Engine", "Binaries", "ThirdParty", "Oodle"];
    private static readonly string[] ProjectBinaries = ["Binaries", "Win64"];

    public static OodleLookup NotFound { get; } = new(null);

    [GeneratedRegex(@"^oo2core_(?<version>\d{1,3})_win64\.dll$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();

    /// <summary>The DLL for <paramref name="pakPath"/>: the environment variable when set (and used as is, or rejected), else the best copy found walking up from the pak's folder.</summary>
    public static OodleLookup Locate(string? pakPath, Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        var configured = getEnvironmentVariable(EnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(configured))
            return IsUsableDll(configured) ? new OodleLookup(Path.GetFullPath(configured)) : new OodleLookup(null, configured);

        return string.IsNullOrWhiteSpace(pakPath) ? NotFound : new OodleLookup(FindBeside(pakPath));
    }

    /// <summary>The message for a pak read that failed in repak, when the failure is the missing Oodle library; null for any other error.</summary>
    public static string? Explain(string? repakError, OodleLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (repakError == null || !repakError.Contains(RepakMissingText, StringComparison.Ordinal)) return null;

        if (lookup.Path != null) return $"Could not load {lookup.Path}. Use a 64-bit {FilePattern}.";
        if (lookup.RejectedEnvironmentValue != null) return $"{EnvironmentVariable} is not an existing {FilePattern} file. Fix or unset it.";
        return $"Oodle DLL not found. Point {EnvironmentVariable} at the game's {FilePattern}.";
    }

    private static bool IsUsableDll(string path) =>
        Path.IsPathFullyQualified(path) && FileNameRegex().IsMatch(Path.GetFileName(path)) && File.Exists(path);

    private static string? FindBeside(string pakPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(pakPath));
        for (var level = 0; folder != null && level <= MaxParentLevels; level++, folder = Path.GetDirectoryName(folder))
        {
            if (BestIn(folder) is { } found) return found;
        }

        return null;
    }

    /// <summary>The highest-numbered DLL in the usual Unreal spots under <paramref name="root"/>, if any.</summary>
    private static string? BestIn(string root)
    {
        var flat = new EnumerationOptions { IgnoreInaccessible = true };
        var deep = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = OodleFolderDepth };

        var candidates = Files(root, flat)
            .Concat(Files(Path.Combine([root, .. OodleFolder]), deep));
        if (Directory.Exists(root))
        {
            foreach (var project in Directory.EnumerateDirectories(root, "*", flat))
                candidates = candidates.Concat(Files(Path.Combine([project, .. ProjectBinaries]), flat));
        }

        return candidates
            .Select(path => (Path: path, Version: VersionOf(path)))
            .Where(c => c.Version >= 0)
            .OrderByDescending(c => c.Version)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Path)
            .FirstOrDefault();
    }

    private static IEnumerable<string> Files(string folder, EnumerationOptions options) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, FilePattern, options) : [];

    /// <summary>The number in the file name, or -1 when the name is not exactly <c>oo2core_&lt;n&gt;_win64.dll</c> (the search pattern alone also matches 8.3 short names).</summary>
    private static int VersionOf(string path)
    {
        var match = FileNameRegex().Match(Path.GetFileName(path));
        return match.Success ? int.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
    }
}
