namespace UAssetEditor.Core.AssetSources;

internal static class BackupPathResolver
{
    /// <summary>Resolves where a backup of <paramref name="filePath"/> should be written: alongside it (".bak" suffix) or under <paramref name="backupFolder"/> if given.</summary>
    public static string Resolve(string filePath, string? backupFolder)
    {
        if (string.IsNullOrEmpty(backupFolder))
            return filePath + ".bak";

        Directory.CreateDirectory(backupFolder);
        return Path.Combine(backupFolder, Path.GetFileName(filePath) + ".bak");
    }

    /// <summary>Where the file as it was before its first save is kept: the same place as <see cref="Resolve"/>, with an ".orig.bak" suffix.</summary>
    public static string ResolveOriginal(string filePath, string? backupFolder) =>
        Path.ChangeExtension(Resolve(filePath, backupFolder), ".orig.bak");
}
