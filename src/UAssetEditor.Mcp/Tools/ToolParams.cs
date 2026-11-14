using UAssetAPI;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Parameter descriptions shared by the tools, so every tool words them the same way.</summary>
internal static class ToolParams
{
    public const string Game =
        "Game profile: rivals, ff7r or stellarblade. Fills in the engine version, the AES key of pak tools and, when usmap is not given, the newest usmap stored for the game (see usmap_list). Omit for an unrelated game.";

    public const string EngineVersion =
        "UAssetAPI EngineVersion name, e.g. VER_UE5_3. Defaults to the game's version, else VER_UE4_27, which is rarely right: pass the game or this.";

    public const string Usmap =
        "Absolute path of a .usmap. Required to read unversioned assets (Marvel Rivals and most UE5 games), or use game with a stored usmap; without one such assets open but read as empty.";

    public const string Strict =
        "Fail with the real parse error instead of opening an asset without its property data.";

    public const string AesKey =
        "AES key in hex for an encrypted pak that is not the game's. Never echoed or logged.";

    public const string Overwrite = "Replace the output when it already exists. Without it an existing output is refused.";
}

/// <summary>Opens assets for the tools, through the same code uacli uses.</summary>
internal static class ToolAssets
{
    public static (string Path, UAsset Asset) Open(string? file, OpenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var path = ToolPaths.ExistingFile(file, nameof(file), ".uasset", ".umap");
        return (path, AssetIo.Open(path, settings.Args));
    }

    public static int ResolveExport(UAsset asset, string? export) =>
        AssetIo.ResolveExportIndex(asset, ToolInput.Required(export, nameof(export), 200));
}
