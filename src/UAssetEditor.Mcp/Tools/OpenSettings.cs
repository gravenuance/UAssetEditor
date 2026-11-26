using System.Globalization;
using ModelContextProtocol;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.AssetSources;
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

    private static readonly UsmapHints McpUsmapHints = new("usmap", _ => "usmap_add");

    private readonly StringWriter _diagnostics;

    private OpenSettings(ArgReader args, StringWriter diagnostics, GameProfile? profile, string? usmapPath)
    {
        Args = args;
        _diagnostics = diagnostics;
        Profile = profile;
        UsmapPath = usmapPath;
    }

    public ArgReader Args { get; }

    public GameProfile? Profile { get; }

    public Game Game => Profile?.Game ?? Game.None;

    /// <summary>The usmap in use: the one given, else the newest stored for the game, else null.</summary>
    public string? UsmapPath { get; }

    public EngineVersion EngineVersion => AssetIo.ResolveVersion(Args);

    /// <summary>The AES key: the one given, else the game's. Never returned to the caller or logged.</summary>
    public byte[]? AesKey => AssetIo.ResolveAesKey(Args);

    /// <summary>The game's pak format, else the standard one.</summary>
    public PakVariant PakVariant => AssetIo.ResolvePakVariant(Args);

    /// <param name="aesKey">Hex, for a pak whose key is not the game's; kept in memory only.</param>
    public static OpenSettings Create(MappingsStore store, string? game, string? engineVersion, string? usmap, bool strict = false, string? aesKey = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        var arguments = new List<string>();

        var profile = ParseGame(game);
        if (profile != null) arguments.AddRange(["--game", profile.CliName]);

        if (ToolInput.Optional(engineVersion, nameof(engineVersion), 60) is { } versionText)
            arguments.AddRange(["--version", ParseEngineVersion(versionText).ToString()]);

        if (ToolInput.Optional(usmap, nameof(usmap)) is { } given) arguments.AddRange(["--usmap", ToolPaths.ExistingFile(given, nameof(usmap), ".usmap")]);

        if (ToolInput.Optional(aesKey, nameof(aesKey), 200) is { } keyText) arguments.AddRange(["--aes", ValidateKey(keyText)]);

        // Last, so a value-less switch cannot take a following value.
        if (strict) arguments.Add("--strict");

        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        var args = new ArgReader(arguments) { Diagnostics = diagnostics, Mappings = store, UsmapHints = McpUsmapHints };
        // Resolved up front, so the call reports the usmap it uses even when it opens nothing; the reader keeps the answer for the open.
        var usmapPath = AssetIo.ResolveUsmapPath(args);

        return new OpenSettings(args, diagnostics, profile, usmapPath);
    }

    /// <summary>Everything worth telling the caller: what was picked for them, then the open's own warnings, each on one line.</summary>
    public IReadOnlyList<string> Warnings()
    {
        var lines = _diagnostics.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. lines.Select(line => Untrusted.Clean(line, 500)).Distinct(StringComparer.Ordinal).Take(MaxWarnings)];
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
        ToolInput.TryOneName<EngineVersion>(text, out var version)
            ? version
            : throw new McpException($"Unknown engineVersion '{Untrusted.Clean(text, 60)}': expected a UAssetAPI EngineVersion name such as VER_UE5_3.");

    /// <summary>Checks a given key once, up front, so a bad one fails the call before anything opens; a key given but empty (just "0x") is refused too.</summary>
    private static string ValidateKey(string text)
    {
        try
        {
            return PakAesKey.Parse(text) is null ? throw new McpException(PakAesKey.Requirement) : text;
        }
        catch (FormatException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
