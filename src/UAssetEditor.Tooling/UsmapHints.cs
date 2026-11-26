using UAssetEditor.Core.Games;

namespace UAssetEditor.Tooling;

/// <summary>How a front end's caller passes a usmap (<see cref="Pass"/>) and stores one for a game (<see cref="Store"/>), for the warnings that tell them to.</summary>
internal sealed record UsmapHints(string Pass, Func<GameProfile, string> Store)
{
    public static UsmapHints Cli { get; } = new("--usmap", profile => $"'uacli usmap add <file> --game {profile.CliName}'");
}
