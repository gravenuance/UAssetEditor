using System.Globalization;
using System.Text.RegularExpressions;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.Games;

/// <summary>One usmap in the <see cref="MappingsStore"/>: the game build it was dumped from and where it lives.</summary>
public sealed record StoredMappings(int Build, string Path);

/// <summary>A usmap could not be stored or found; the message says what to do about it.</summary>
public sealed class MappingsStoreException : Exception
{
    public MappingsStoreException() { }

    public MappingsStoreException(string message) : base(message) { }

    public MappingsStoreException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The known place for each game's current usmap: one folder per <see cref="GameProfile.CliName"/> under a root
/// (<c>UASSETEDITOR_MAPPINGS_DIR</c>, else <c>%LOCALAPPDATA%\UAssetEditor\Mappings</c>). The game build is part of
/// every stored file name, so the newest usmap is derivable from the folder alone.
/// </summary>
public sealed partial class MappingsStore
{
    public const string RootEnvironmentVariable = "UASSETEDITOR_MAPPINGS_DIR";

    private const string Extension = ".usmap";

    public MappingsStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>The store at the environment variable's folder if set, else under <c>%LOCALAPPDATA%\UAssetEditor</c>.</summary>
    public static MappingsStore Default()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        return new MappingsStore(!string.IsNullOrWhiteSpace(fromEnvironment)
            ? fromEnvironment
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UAssetEditor", "Mappings"));
    }

    public string DirectoryFor(GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return Path.Combine(Root, game.CliName);
    }

    /// <summary>
    /// The game build a usmap file name carries: a Dumper-7 name (<c>5.3.2-3939753+++depot_marvel+S10.5_release-Marvel.usmap</c>
    /// gives 3939753) or the <c>&lt;build&gt;-</c> prefix <see cref="Add"/> puts on a file whose name lacks it. Null when neither.
    /// </summary>
    public static int? ParseBuild(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var match = DumperName().Match(fileName);
        if (!match.Success) match = BuildPrefix().Match(fileName);
        return match.Success && int.TryParse(match.Groups["build"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var build) ? build : null;
    }

    /// <summary>
    /// Copies <paramref name="sourceFile"/> into the game's folder, keeping its name (prefixed with the build when the name
    /// has none). The build comes from the name or <paramref name="build"/>. The copy is validated as a usmap and then
    /// renamed into place, so a failure never leaves a partial file. A build that is already stored needs <paramref name="force"/>.
    /// </summary>
    public StoredMappings Add(string sourceFile, GameProfile game, int? build = null, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentNullException.ThrowIfNull(game);

        if (!File.Exists(sourceFile)) throw new MappingsStoreException($"File not found: {sourceFile}");
        var fileName = Path.GetFileName(sourceFile);
        if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            throw new MappingsStoreException($"Not a usmap (expects a {Extension} file): {fileName}");
        if (build is <= 0) throw new MappingsStoreException($"Build must be a positive number, not {build}.");

        var fromName = ParseBuild(fileName);
        if (build != null && fromName != null && build != fromName)
            throw new MappingsStoreException($"{fileName} is named for build {fromName}, but build {build} was given.");
        var resolved = build ?? fromName
            ?? throw new MappingsStoreException($"Cannot tell which game build {fileName} is from: its name has none (a Dumper-7 name looks like 5.3.2-3939753+++...). Give the build number (--build N).");

        var existing = List(game).Where(entry => entry.Build == resolved).ToList();
        if (existing.Count > 0 && !force)
            throw new MappingsStoreException($"Build {resolved} is already stored as {existing[0].Path}. Replace it with --force.");

        var directory = DirectoryFor(game);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, fromName != null ? fileName : $"{resolved}-{fileName}");
        var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(sourceFile, temp);
            Validate(temp, fileName);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        // Another file of the same build (a different name) is what force replaces.
        foreach (var stale in existing.Where(entry => !string.Equals(entry.Path, target, StringComparison.OrdinalIgnoreCase)))
            File.Delete(stale.Path);
        return new StoredMappings(resolved, target);
    }

    /// <summary>The game's stored usmaps, highest build first. Files whose name carries no build are not listed.</summary>
    public IReadOnlyList<StoredMappings> List(GameProfile game)
    {
        var directory = DirectoryFor(game);
        if (!Directory.Exists(directory)) return [];

        var entries = new List<StoredMappings>();
        foreach (var path in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (ParseBuild(Path.GetFileName(path)) is { } build) entries.Add(new StoredMappings(build, path));
        }
        entries.Sort((a, b) => a.Build != b.Build ? b.Build.CompareTo(a.Build) : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    /// <summary>The stored usmap with the highest build, or null when the game has none.</summary>
    public StoredMappings? Latest(GameProfile game) => List(game) is { Count: > 0 } entries ? entries[0] : null;

    private static void Validate(string path, string displayName)
    {
        try
        {
            _ = new Usmap(path);
        }
        catch (Exception ex)
        {
            throw new MappingsStoreException($"{displayName} is not a readable usmap: {ex.Message}", ex);
        }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+-(?<build>\d+)\+\+\+", RegexOptions.CultureInvariant)]
    private static partial Regex DumperName();

    [GeneratedRegex(@"^(?<build>\d+)-", RegexOptions.CultureInvariant)]
    private static partial Regex BuildPrefix();
}
