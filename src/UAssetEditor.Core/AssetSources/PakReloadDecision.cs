using UAssetAPI;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// What a front end does about an open pak when its key or game settings change: reopen it in the wanted <see cref="PakFormat"/>,
/// leave it alone, or leave it alone and report that the key text is unusable. Taken from the settings and the open pak alone,
/// so a bad key never stops the other settings (engine version, usmap) from being applied.
/// </summary>
public sealed record PakReloadDecision
{
    private PakReloadDecision(PakFormat? reopenAs, string? keyProblem)
    {
        ReopenAs = reopenAs;
        KeyProblem = keyProblem;
    }

    /// <summary>The format to reopen the pak in, or null when the open pak already is in the wanted one (or the key is unusable).</summary>
    public PakFormat? ReopenAs { get; }

    /// <summary>Why the key text can't be used, or null. The open pak keeps its format then.</summary>
    public string? KeyProblem { get; }

    public bool NeedsReopen => ReopenAs is not null;

    /// <summary>Whether the open pak is not in the format the settings ask for, whether or not it can be reopened in it.</summary>
    public bool OpenPakDiffersFromSettings => ReopenAs is not null || KeyProblem is not null;

    /// <summary>
    /// Compares the open pak's <paramref name="openFormat"/> with what the settings ask for: the selected game's variant (the
    /// open pak's own when no game is selected) and the key text. Never throws for key text that is not hex.
    /// </summary>
    public static PakReloadDecision For(PakFormat openFormat, GameProfile? profile, string? aesKeyHex)
    {
        ArgumentNullException.ThrowIfNull(openFormat);

        PakFormat wanted;
        try
        {
            wanted = PakFormat.Resolve(profile, aesKeyHex, openFormat.Variant);
        }
        catch (FormatException)
        {
            return new PakReloadDecision(null, "The AES key is not a valid hex string.");
        }

        return new PakReloadDecision(wanted.Equals(openFormat) ? null : wanted, null);
    }
}
