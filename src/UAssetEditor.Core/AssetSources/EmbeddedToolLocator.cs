using System.Reflection;
using System.Security.Cryptography;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Shared logic for locating a native tool this app bundles (the pak worker, retoc): check an
/// env var override first, then an embedded resource (only present in a real single-file
/// publish - see the relevant PublishSingleFile-conditioned ItemGroup in UAssetEditor.App.csproj),
/// extracted once per content hash into a LocalAppData folder so repeat launches never pay the
/// extraction cost again, or finally fall back to a caller-supplied dev-environment lookup for
/// a plain `dotnet build`/`dotnet run`/`dotnet test`, which never has anything embedded.
/// </summary>
public static class EmbeddedToolLocator
{
    public static string Resolve(string embeddedResourceName, string envVarOverrideName, Func<string?> devFallback)
    {
        ArgumentNullException.ThrowIfNull(embeddedResourceName);
        ArgumentNullException.ThrowIfNull(envVarOverrideName);
        ArgumentNullException.ThrowIfNull(devFallback);

        var overridePath = Environment.GetEnvironmentVariable(envVarOverrideName);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var embedded = TryExtractEmbedded(embeddedResourceName);
        if (embedded != null) return embedded;

        var fallback = devFallback();
        if (fallback != null) return fallback;

        throw new FileNotFoundException(
            $"Could not locate {embeddedResourceName} via {envVarOverrideName}, an embedded resource, or a dev-environment fallback.");
    }

    /// <summary>
    /// Walks up from the running assembly's own directory looking for a sibling "src" folder
    /// (this repo's known layout - works whether the running assembly sits under a `src\` or
    /// `tests\` folder), then hands it to <paramref name="resolveFromSrcDir"/> to locate
    /// whatever dev-environment copy of the tool that caller knows how to find under there.
    /// </summary>
    public static string? FindUnderSrcSibling(Func<DirectoryInfo, string?> resolveFromSrcDir)
    {
        ArgumentNullException.ThrowIfNull(resolveFromSrcDir);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidateSrc = dir.Parent?.EnumerateDirectories("src", SearchOption.TopDirectoryOnly).FirstOrDefault()
                ?? (dir.Name == "src" ? dir : null);
            if (candidateSrc != null)
            {
                var found = resolveFromSrcDir(candidateSrc);
                if (found != null) return found;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? TryExtractEmbedded(string embeddedResourceName)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        using var resourceStream = entryAssembly?.GetManifestResourceStream(embeddedResourceName);
        if (resourceStream == null) return null;

        using var buffered = new MemoryStream();
        resourceStream.CopyTo(buffered);
        var bytes = buffered.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];

        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UAssetEditor", "Tools", embeddedResourceName, hash);
        var targetPath = Path.Combine(targetDir, embeddedResourceName);

        if (File.Exists(targetPath)) return targetPath;

        Directory.CreateDirectory(targetDir);
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, targetPath, overwrite: true);
        return targetPath;
    }
}
