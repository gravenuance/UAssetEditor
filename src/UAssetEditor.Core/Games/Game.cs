namespace UAssetEditor.Core.Games;

/// <summary>A game whose packages need settings beyond what the engine version alone implies. <see cref="None"/> is "no game selected": nothing is preset and nothing special happens on open.</summary>
public enum Game
{
    None,
    MarvelRivals,
    FinalFantasy7Remake,
}
