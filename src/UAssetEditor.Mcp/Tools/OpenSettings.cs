using System.Globalization;
using ModelContextProtocol;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Games;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>
/// How a tool call opens assets: the game, engine version and usmap, validated once and handed on as the same
/// <see cref="ArgReader"/> uacli builds from its global options, so both resolve them identically. Warnings the open
/// raises are collected here instead of going to a terminal, for the tool to return. The usmap shared by every call comes
/// from <see cref="AssetIo"/>'s per-process cache; which stored build is newest is looked up on every call, so a
/// <c>usmap_add</c> takes effect at once.
/// </summary>
internal sealed class OpenSettings
{
    private const int MaxWarnings = 20;
    private const int AesKeyBytes = 32;

    private readonly StringWriter _diagnostics;
    private readonly List<string> _notes;

    private OpenSettings(ArgReader args, StringWriter diagnostics, GameProfile? profile, string? usmapPath, List<string> notes)
    {
        Args = args;
        _diagnostics = diagnostics;
        Profile = profile;
        UsmapPath = usmapPath;
        _notes = notes;
    }

    public ArgReader Args { get; }

    public GameProfile? Profile { get; }

    public Game Game => Profile?.Game ?? Game.None;

    /// <summary>The usmap in use: the one given, else the newest stored for the game, else null.</summary>
    public string? UsmapPath { get; }

    public EngineVersion EngineVersion => AssetIo.ResolveVersion(Args);

    /// <summary>The AES key: the one given, else the game's. Never returned to the caller or logged.</summary>
    public byte[]? AesKey => AssetIo.ResolveAesKey(Args);

    /// <param name="aesKey">Hex, for a pak whose key is not the game's; kept in memory only.</param>
    public static OpenSettings Create(MappingsStore store, string? game, string? engineVersion, string? usmap, bool strict = false, string? aesKey = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var notes = new List<string>();
        var arguments = new List<string>();

        var profile = ParseGame(game);
        if (profile != null) arguments.AddRange(["--game", profile.CliName]);

        if (ToolInput.Optional(engineVersion, nameof(engineVersion), 60) is { } versionText)
            arguments.AddRange(["--version", ParseEngineVersion(versionText).ToString()]);

        var usmapPath = ResolveUsmap(store, profile, usmap, notes);
        if (usmapPath != null) arguments.AddRange(["--usmap", usmapPath]);

        if (ToolInput.Optional(aesKey, nameof(aesKey), 200) is { } keyText) arguments.AddRange(["--aes", ValidateHex(keyText)]);

        // Last, so a value-less switch cannot take a following value.
        if (strict) arguments.Add("--strict");

        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        var args = new ArgReader(arguments) { Diagnostics = diagnostics, Mappings = store };
        // The store was already read for this call (with its own note), so the open must not look again and warn twice.
        if (profile != null && usmapPath == null) args.StoredUsmaps[profile.CliName] = new Lazy<string?>(() => null);

        return new OpenSettings(args, diagnostics, profile, usmapPath, notes);
    }

    /// <summary>Everything worth telling the caller: what was picked for them, then the open's own warnings, each on one line.</summary>
    public IReadOnlyList<string> Warnings()
    {
        var lines = _diagnostics.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. _notes.Concat(lines).Select(line => Untrusted.Clean(line, 500)).Distinct(StringComparer.Ordinal).Take(MaxWarnings)];
    }

    /// <summary>The profile a tool's game argument names, or null when it is omitted.</summary>
    public static GameProfile? ParseGame(string? game)
    {
        var text = ToolInput.Optional(game, nameof(game), 40);
        if (text == null) return null;
        return GameProfile.FindByCliName(text)
            ?? throw new McpException($"Unknown game '{Untrusted.Clean(text, 40)}': expected one of {GameProfile.ValidNames}.");
    }

    private static EngineVersion ParseEngineVersion(string text) =>
        !char.IsDigit(text[0]) && Enum.TryParse<EngineVersion>(text, ignoreCase: true, out var version) && Enum.IsDefined(version)
            ? version
            : throw new McpException($"Unknown engineVersion '{Untrusted.Clean(text, 60)}': expected a UAssetAPI EngineVersion name such as VER_UE5_3.");

    private static string? ResolveUsmap(MappingsStore store, GameProfile? profile, string? usmap, List<string> notes)
    {
        if (ToolInput.Optional(usmap, nameof(usmap)) is { } given) return ToolPaths.ExistingFile(given, nameof(usmap), ".usmap");
        if (profile == null) return null;

        if (store.Latest(profile) is not { } stored)
        {
            notes.Add($"No usmap is stored for {profile.CliName} ({store.DirectoryFor(profile)}); assets that use unversioned properties will read as empty. Pass usmap, or store one with usmap_add.");
            return null;
        }

        notes.Add($"Using stored usmap {stored.Path} ({AssetIo.DescribeBuild(stored)}).");
        return stored.Path;
    }

    private static string ValidateHex(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        const string Refusal = "aesKey must be exactly 32 bytes: 64 hex digits, with or without a leading 0x.";
        try
        {
            return Convert.FromHexString(hex).Length == AesKeyBytes ? text : throw new McpException(Refusal);
        }
        catch (FormatException ex)
        {
            throw new McpException(Refusal, ex);
        }
    }
}
