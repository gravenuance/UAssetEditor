using System.Text.Json;
using System.Text.Json.Serialization;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Cli;

/// <summary>Shared open/save/export-resolution plumbing every command needs, kept out of each command so the interesting logic isn't buried under it.</summary>
internal static class AssetIo
{
    /// <summary>
    /// Same shape the app's own batch-edit tab saves/loads (<c>EditRule</c> is already
    /// JSON-polymorphic on a "kind" discriminator) - a ruleset written by one reads back in
    /// the other. Adds <see cref="JsonStringEnumConverter"/> on top, which the app doesn't
    /// use (its enums serialize as bare numbers): it still reads an app-authored file fine
    /// (the converter accepts numbers on input) while making a hand-authored ruleset - the
    /// normal way to drive this CLI - readable ("Contains" instead of "0").
    /// </summary>
    public static readonly JsonSerializerOptions RuleSetJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static EngineVersion ResolveVersion(ArgReader args)
    {
        var text = args.Option("version") ?? "VER_UE4_27";
        return Enum.TryParse<EngineVersion>(text, ignoreCase: true, out var version)
            ? version
            : throw new ArgException($"Unknown --version '{text}' (expects a UAssetAPI.EngineVersion name, e.g. VER_UE4_27).");
    }

    public static Usmap? ResolveMappings(ArgReader args)
    {
        var path = args.Option("usmap");
        if (path == null) return null;
        if (!File.Exists(path)) throw new ArgException($"--usmap file not found: {path}");
        return new Usmap(path);
    }

    public static byte[]? ResolveAesKey(ArgReader args) => PakAesKey.Parse(args.Option("aes") ?? "");

    public static PakVersion ResolvePakVersion(ArgReader args)
    {
        var text = args.Option("pak-version") ?? "V11";
        return Enum.TryParse<PakVersion>(text, ignoreCase: true, out var version)
            ? version
            : throw new ArgException($"Unknown --pak-version '{text}' (expects a UAssetAPI.PakVersion name, e.g. V11).");
    }

    public static PakCompression[]? ResolveCompression(ArgReader args)
    {
        var text = args.Option("compression");
        if (text == null) return null;
        return Enum.TryParse<PakCompression>(text, ignoreCase: true, out var compression)
            ? [compression]
            : throw new ArgException($"Unknown --compression '{text}' (expects Zlib, Gzip, Oodle, or Zstd).");
    }

    public static UAsset Open(string path, ArgReader args)
    {
        if (!File.Exists(path)) throw new ArgException($"File not found: {path}");
        return ResilientAssetLoader.Open(path, ResolveVersion(args), ResolveMappings(args));
    }

    public static void Save(UAsset asset, string path, bool backup)
    {
        if (backup)
        {
            File.Copy(path, path + ".bak", overwrite: true);
            // The actual property data for anything beyond a tiny asset lives in the .uexp
            // companion, which Write() below also rewrites - back it up too, or a restore from
            // the .uasset.bak alone would pair stale property bytes with the post-edit .uexp.
            var uexpPath = Path.ChangeExtension(path, ".uexp");
            if (File.Exists(uexpPath)) File.Copy(uexpPath, uexpPath + ".bak", overwrite: true);
        }
        asset.Write(path);
    }

    /// <summary>Resolves an --export value as either a 0-based index or a (sub)string match against export names - whichever the caller finds more convenient to type.</summary>
    public static int ResolveExportIndex(UAsset asset, string value)
    {
        if (int.TryParse(value, out var index))
        {
            if (index < 0 || index >= asset.Exports.Count)
                throw new ArgException($"--export {index} is out of range (0..{asset.Exports.Count - 1}).");
            return index;
        }

        var matches = new List<int>();
        for (var i = 0; i < asset.Exports.Count; i++)
        {
            var name = asset.Exports[i].ObjectName.Value?.Value ?? "";
            if (name.Contains(value, StringComparison.OrdinalIgnoreCase))
                matches.Add(i);
        }

        return matches.Count switch
        {
            0 => throw new ArgException($"No export name contains '{value}'."),
            1 => matches[0],
            _ => throw new ArgException(
                $"'{value}' matches {matches.Count} exports ({string.Join(", ", matches.Select(i => $"{i}:{asset.Exports[i].ObjectName.Value?.Value}"))}) - use an index instead."),
        };
    }
}
