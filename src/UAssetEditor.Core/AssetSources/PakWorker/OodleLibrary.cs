using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UAssetEditor.Core.Logging;

namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>What <see cref="OodleLibrary.Locate"/> found: the DLL to hand the pak worker, or why there is none.</summary>
/// <param name="Path">The full path of a usable <c>oo2core_*_win64.dll</c>, else null.</param>
/// <param name="RejectedEnvironmentValue">What <c>REPAK_OODLE_DLL</c> held when it is set but is not a usable DLL; the lookup then stops there instead of guessing.</param>
public sealed record OodleLookup(string? Path, string? RejectedEnvironmentValue = null);

/// <summary>
/// Finds the user's own Oodle DLL for legacy paks that use it (Days Gone). Oodle is Epic/RAD's proprietary library,
/// so it is never bundled: the order is <c>REPAK_OODLE_DLL</c>, else a copy inside the install of the game the pak is in.
/// Only a regular file named <c>oo2core_&lt;n&gt;_win64.dll</c> is accepted, and neither PATH nor the working folder is searched.
/// </summary>
public static partial class OodleLibrary
{
    public const string EnvironmentVariable = "REPAK_OODLE_DLL";
    public const string FilePattern = "oo2core_*_win64.dll";

    /// <summary>The pak's own folder and this many parents are examined for the game's root: enough for <c>&lt;root&gt;/&lt;Game&gt;/Content/Paks/~mods</c> and the Rivals layout.</summary>
    internal const int MaxParentLevels = 5;

    /// <summary>How deep under <c>Engine/Binaries/ThirdParty/Oodle</c> to look (Unreal's own layout is <c>Win64/</c>).</summary>
    private const int OodleFolderDepth = 3;

    /// <summary>repak's error text when its Oodle lookup fails; its only cause is a DLL that is missing or will not load.</summary>
    private const string RepakMissingText = "OodleLZ_Decompress was not provided";

    private static readonly string[] OodleFolder = ["Engine", "Binaries", "ThirdParty", "Oodle"];
    private static readonly string[] ProjectBinaries = ["Binaries", "Win64"];

    /// <summary>A DLL found for a pak's folder is reused while the file exists; a failed search is not remembered, so a DLL the user adds later is found.</summary>
    private static readonly ConcurrentDictionary<string, string> FoundByPakFolder = new(StringComparer.OrdinalIgnoreCase);

    public static OodleLookup NotFound { get; } = new(null);

    [GeneratedRegex(@"^oo2core_(?<version>\d{1,3})_win64\.dll$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();

    /// <summary>
    /// The DLL for <paramref name="pakPath"/>: the environment variable when set (used as is, or rejected), else the best
    /// copy inside the game's root. Without a pak only the variable is consulted, which costs no file-system walk.
    /// </summary>
    public static OodleLookup Locate(string? pakPath, Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        var configured = getEnvironmentVariable(EnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(configured))
            return IsUsableDll(configured) ? new OodleLookup(Path.GetFullPath(configured)) : new OodleLookup(null, configured);

        return string.IsNullOrWhiteSpace(pakPath) ? NotFound : new OodleLookup(FindInGameRoot(pakPath));
    }

    /// <summary>True for repak's failure to reach Oodle: no DLL was given, or the one given would not load.</summary>
    public static bool IsMissingError(string? repakError) =>
        repakError != null && repakError.Contains(RepakMissingText, StringComparison.Ordinal);

    /// <summary>The message for a pak read that failed in repak, when the failure is the missing Oodle library; null for any other error.</summary>
    public static string? Explain(string? repakError, OodleLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (!IsMissingError(repakError)) return null;

        if (lookup.Path != null) return $"Could not load {lookup.Path}. Use a 64-bit {FilePattern}.";
        if (lookup.RejectedEnvironmentValue != null) return $"{EnvironmentVariable} is not an existing {FilePattern} file. Fix or unset it.";
        return $"Oodle DLL not found. Point {EnvironmentVariable} at the game's {FilePattern}.";
    }

    private static bool IsUsableDll(string path) =>
        Path.IsPathFullyQualified(path) && FileNameRegex().IsMatch(Path.GetFileName(path)) && File.Exists(path);

    /// <summary>
    /// The best DLL inside the nearest game root above the pak, if any. The search stops at the first root, so it never
    /// lists folders above one, and a drive or share root is never a game root. A search that fails (offline share,
    /// broken link, path too long) finds nothing rather than stopping the pak from opening.
    /// </summary>
    private static string? FindInGameRoot(string pakPath)
    {
        try
        {
            var pakFolder = Path.GetDirectoryName(Path.GetFullPath(pakPath));
            if (pakFolder != null && FoundByPakFolder.TryGetValue(pakFolder, out var known) && File.Exists(known)) return known;

            string? previous = null;
            var folder = pakFolder;
            for (var level = 0; folder != null && level <= MaxParentLevels; level++, previous = folder, folder = Path.GetDirectoryName(folder))
            {
                if (Path.GetDirectoryName(folder) == null) return null; // a drive or share root
                if (!LooksLikeGameRoot(folder, previous)) continue;

                var found = BestIn(folder);
                if (found != null) FoundByPakFolder[pakFolder!] = found;
                return found;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogSearchFailed(AppLog.For<OodleLookup>(), pakPath, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Looking for an Oodle DLL near {Pak} failed")]
    private static partial void LogSearchFailed(ILogger logger, string pak, Exception exception);

    /// <summary>
    /// An Unreal install's top folder: it has <c>Engine</c> or a <c>.uproject</c>, or the folder just below it on the way
    /// down to the pak (<paramref name="child"/>) is a project folder holding both <c>Content</c> and <c>Binaries/Win64</c>.
    /// Only folders on the path are looked at; a folder's other children are never listed.
    /// </summary>
    private static bool LooksLikeGameRoot(string folder, string? child) =>
        Directory.Exists(Path.Combine(folder, "Engine"))
        || Directory.EnumerateFiles(folder, "*.uproject", new EnumerationOptions { IgnoreInaccessible = true }).Any()
        || (child != null && IsProjectFolder(child));

    private static bool IsProjectFolder(string folder) =>
        Directory.Exists(Path.Combine(folder, "Content")) && Directory.Exists(Path.Combine([folder, .. ProjectBinaries]));

    /// <summary>The highest-numbered DLL in the usual Unreal spots under <paramref name="root"/>, if any.</summary>
    private static string? BestIn(string root)
    {
        var flat = new EnumerationOptions { IgnoreInaccessible = true };
        var deep = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = OodleFolderDepth };

        var candidates = Files(root, flat)
            .Concat(Files(Path.Combine([root, .. OodleFolder]), deep));
        foreach (var project in Directory.EnumerateDirectories(root, "*", flat))
            candidates = candidates.Concat(Files(Path.Combine([project, .. ProjectBinaries]), flat));

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
        return match.Success ? int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture) : -1;
    }
}
