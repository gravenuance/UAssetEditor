using System.Collections.Concurrent;
using System.Reflection;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Shared logic for locating a native tool this app bundles (the pak worker, retoc): check an
/// env var override first, then an embedded resource (only present in a real single-file
/// publish - see the relevant PublishSingleFile-conditioned ItemGroup in UAssetEditor.App.csproj),
/// extracted once per build into a LocalAppData folder so repeat launches never pay the
/// extraction cost again, or finally fall back to a caller-supplied dev-environment lookup for
/// a plain `dotnet build`/`dotnet run`/`dotnet test`, which never has anything embedded.
/// </summary>
public static class EmbeddedToolLocator
{
    /// <summary>
    /// Embedded-or-fallback resolutions, memoized per resource name for this process's lifetime -
    /// <see cref="RetocProcess"/> re-resolves its executable on every single invocation (unlike
    /// <see cref="PakWorkerProcess"/>, which only resolves once per spawn), so without this every
    /// retoc call would repeat the extraction/fallback lookup from scratch. The env var override
    /// is deliberately NOT part of this cache - it's just a cheap env read plus a File.Exists,
    /// nowhere near the cost this cache exists to avoid, and caching it would mean a value set or
    /// changed after this process's first resolution is silently ignored for the rest of the run.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> ResolvedPaths = new(StringComparer.Ordinal);

    public static string Resolve(string embeddedResourceName, string envVarOverrideName, Func<string?> devFallback)
    {
        ArgumentNullException.ThrowIfNull(embeddedResourceName);
        ArgumentNullException.ThrowIfNull(envVarOverrideName);
        ArgumentNullException.ThrowIfNull(devFallback);

        var overridePath = Environment.GetEnvironmentVariable(envVarOverrideName);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        return ResolvedPaths.GetOrAdd(embeddedResourceName, _ => ResolveEmbeddedOrFallback(embeddedResourceName, envVarOverrideName, devFallback));
    }

    private static string ResolveEmbeddedOrFallback(string embeddedResourceName, string envVarOverrideName, Func<string?> devFallback)
    {
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
        if (entryAssembly == null) return null;

        // Keyed by the assembly's build identity (a GUID stamped in at compile time), not a
        // hash of the resource content - this lets the cache-hit path (the overwhelmingly
        // common case: every retoc/worker call after the first, across every relaunch of the
        // same published build) skip touching the embedded resource at all, rather than having
        // to read and hash the ~7MB payload just to find out it was already extracted.
        var buildId = entryAssembly.ManifestModule.ModuleVersionId.ToString("N")[..16];
        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UAssetEditor", "Tools", embeddedResourceName, buildId);
        var targetPath = Path.Combine(targetDir, embeddedResourceName);

        if (File.Exists(targetPath)) return targetPath;

        using var resourceStream = entryAssembly.GetManifestResourceStream(embeddedResourceName);
        if (resourceStream == null) return null;

        Directory.CreateDirectory(targetDir);
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var tempFile = File.Create(tempPath))
            resourceStream.CopyTo(tempFile);
        File.Move(tempPath, targetPath, overwrite: true);
        return targetPath;
    }
}
