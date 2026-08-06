using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Reads (and, through <see cref="Editing.EditExecutor"/>/<see cref="AssetWorkspace"/>,
/// edits) .uasset entries packed inside a legacy .pak archive, via UAssetAPI's embedded
/// `repak` bindings (<see cref="PakBuilder"/>/<see cref="PakReader"/>). Entries are never
/// written back into the original .pak directly - editing an entry writes to a private
/// temp extraction copy, and <see cref="AssetSources.PakRepacker"/> is what later bundles
/// the (possibly edited) files into a new .pak.
///
/// Archives under <see cref="LargePakThresholdBytes"/> are fully extracted up front so
/// browsing/searching behaves just like a loose folder. Larger archives extract nothing
/// until the caller actually asks for a specific entry (via <see cref="OpenAsset"/>),
/// so a multi-gigabyte pak never needs to be resident in memory or on temp disk all at
/// once - only whatever the user actually opens.
/// </summary>
public sealed class PakAssetSource : IAssetSource, IDisposable
{
    public const long LargePakThresholdBytes = 1_000_000_000; // 1 GB

    private static readonly string[] CompanionExtensions = [".uexp", ".ubulk"];

    private readonly FileStream _stream;
    private readonly PakReader _reader;
    private readonly HashSet<string> _allEntries;
    private readonly List<string> _uassetEntries;
    private readonly Dictionary<string, string> _extractedPaths = new();
    private readonly object _lock = new();
    private bool _disposed;

    public PakAssetSource(string pakPath, byte[]? aesKey = null, long largePakThresholdBytes = LargePakThresholdBytes)
    {
        PakPath = pakPath;
        IsLargePak = new FileInfo(pakPath).Length >= largePakThresholdBytes;
        TempExtractionDirectory = Path.Combine(Path.GetTempPath(), "UAssetEditor_Pak_" + Guid.NewGuid());
        Directory.CreateDirectory(TempExtractionDirectory);

        _stream = File.Open(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var builder = new PakBuilder();
        if (aesKey != null) builder = builder.Key(aesKey);
        _reader = builder.Reader(_stream);

        MountPoint = _reader.GetMountPoint();
        _allEntries = new HashSet<string>(_reader.Files(), StringComparer.Ordinal);
        _uassetEntries = _allEntries.Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).ToList();

        if (!IsLargePak)
        {
            foreach (var entry in _uassetEntries)
                ExtractEntry(entry);
        }
    }

    public string PakPath { get; }
    public string MountPoint { get; }
    public string TempExtractionDirectory { get; }
    public bool IsLargePak { get; }

    /// <summary>Every path in the pak, not just .uasset entries - for a raw tree view where .uexp/.ubulk/other files show up as non-openable leaves.</summary>
    public IReadOnlyCollection<string> ListAllEntries() => _allEntries;

    public IEnumerable<string> EnumerateAssetPaths() => _uassetEntries;

    public UAsset OpenAsset(string assetPath, EngineVersion engineVersion, Usmap? mappings) =>
        ResilientAssetLoader.Open(ExtractEntry(assetPath), engineVersion, mappings);

    public void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder)
    {
        var tempPath = ExtractEntry(assetPath);

        if (createBackup)
            File.Copy(tempPath, BackupPathResolver.Resolve(tempPath, backupFolder), overwrite: true);

        asset.Write(tempPath);
    }

    /// <summary>Resolves an already-extracted temp path for <paramref name="internalPath"/>, if any - used by the repacker to tell edited entries apart from untouched ones.</summary>
    public bool TryGetExtractedPath(string internalPath, out string tempPath)
    {
        lock (_lock)
            return _extractedPaths.TryGetValue(internalPath, out tempPath!);
    }

    /// <summary>Reads an entry's bytes straight from the pak, without caching a temp copy - for the repacker to pass through untouched entries one at a time.</summary>
    public byte[] ReadOriginalBytes(string internalPath)
    {
        lock (_lock)
            return _reader.Get(_stream, internalPath);
    }

    /// <summary>Extracts (or returns the already-cached temp copy of) one entry. Internal-only escape hatch beyond <see cref="OpenAsset"/>/<see cref="SaveAsset"/> - lets tests simulate "this entry was opened/edited" without needing UAssetAPI-parseable bytes, since this step is pure byte-copying and doesn't parse anything.</summary>
    internal string ExtractEntry(string internalPath)
    {
        lock (_lock)
        {
            if (_extractedPaths.TryGetValue(internalPath, out var cached))
                return cached;

            var tempPath = ToTempPath(internalPath);
            WriteEntry(internalPath, tempPath);

            var baseNoExt = internalPath[..internalPath.LastIndexOf('.')];
            foreach (var companionExt in CompanionExtensions)
            {
                var companionInternal = baseNoExt + companionExt;
                if (_allEntries.Contains(companionInternal) && !_extractedPaths.ContainsKey(companionInternal))
                {
                    var companionTemp = ToTempPath(companionInternal);
                    WriteEntry(companionInternal, companionTemp);
                    _extractedPaths[companionInternal] = companionTemp;
                }
            }

            _extractedPaths[internalPath] = tempPath;
            return tempPath;
        }
    }

    private string ToTempPath(string internalPath) =>
        Path.Combine(TempExtractionDirectory, internalPath.Replace('/', Path.DirectorySeparatorChar));

    private void WriteEntry(string internalPath, string tempPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        File.WriteAllBytes(tempPath, _reader.Get(_stream, internalPath));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _reader.Dispose();
        _stream.Dispose();

        try
        {
            Directory.Delete(TempExtractionDirectory, recursive: true);
        }
        catch
        {
            // Best effort - a leftover temp folder isn't worth failing Dispose over.
        }
    }
}
