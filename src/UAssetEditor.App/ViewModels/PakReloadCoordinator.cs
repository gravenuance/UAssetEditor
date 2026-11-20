using Microsoft.Extensions.Logging;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;

namespace UAssetEditor.App.ViewModels;

/// <summary>What the toolbar currently asks for: the settings a reload compares the open workspace with.</summary>
public sealed record ReloadSettings(EngineVersion Version, Game Game, string? UsmapPath, string AesKeyHex);

/// <summary>Where and in which format the current pak was opened.</summary>
public sealed record OpenPakInfo(string PakPath, PakFormat Format);

/// <summary>
/// What <see cref="PakReloadCoordinator{TOpened}"/> needs from the window it serves: the current state, and every side effect
/// (prompts, opening and adopting a pak, status, busy state, the tree), so the reload logic runs without a UI.
/// </summary>
public interface IPakReloadHost<TOpened> where TOpened : class, IDisposable
{
    bool IsBusy { get; set; }

    /// <summary>The open workspace, or null when nothing is open.</summary>
    AssetWorkspace? Workspace { get; }

    /// <summary>The open pak, or null when the workspace is not pak-backed.</summary>
    OpenPakInfo? OpenPak { get; }

    ReloadSettings Settings { get; }

    /// <summary>What the open workspace's resolver was built from.</summary>
    ResolverSettings? AppliedSettings { get; set; }

    string StatusMessage { get; set; }

    bool UsmapFileExists(string path);

    /// <summary>Builds the resolver for <paramref name="settings"/>; throws when the usmap can't be loaded.</summary>
    ResolverBuild BuildResolver(ReloadSettings settings);

    /// <summary>Opens the pak in <paramref name="format"/> with its own workspace; nothing of the current source is touched.</summary>
    Task<TOpened> OpenPakAsync(string pakPath, PakFormat format, ResolverBuild build, CancellationToken cancellationToken);

    /// <summary>True when there are no unsaved edits or the user agrees to lose them.</summary>
    bool ConfirmDiscardUnsavedEdits();

    /// <summary>True when there are no saved-but-unrepacked assets or the user agrees to lose them.</summary>
    bool ConfirmDiscardUnrepackedEdits();

    /// <summary>Makes <paramref name="opened"/> the current pak, taking ownership of it: the old source goes, with its edit tracking, and the tree is rebuilt.</summary>
    Task AdoptPakAsync(TOpened opened);

    void ClearUnsavedEditTracking();

    /// <summary>Collapses the Browse tree's nodes under these assets, whose open instances were dropped.</summary>
    void ResetTreeAssets(IReadOnlyCollection<string> assetPaths);

    Task RefreshOpenContentAsync();

    /// <summary>Stops a reload the debounce timer has scheduled, since the one starting now covers it.</summary>
    void CancelScheduledReload();
}

/// <summary>
/// Reacts to a changed UE version, usmap, game or AES key, in independent steps so one failing never blocks another. (1) A pak
/// whose <see cref="PakFormat"/> is no longer what the settings ask for (<see cref="PakReloadDecision"/>) is reopened: the new
/// source is opened first, and only once that worked is the user asked whether to lose the old one's unrepacked assets; a
/// declined or failed reopen leaves the old source and its edit tracking as they are. (2) A changed engine version or usmap is
/// applied to the current workspace whether or not (1) happened, asking only about unsaved edits, which dropping the open assets
/// loses, never about unrepacked ones, which it keeps. (3) Whatever was last shown is replayed. Deciding by comparing the open
/// pak and workspace with the settings (not by remembering that a setting changed) means a reload that was skipped is simply
/// retried by the next one.
/// </summary>
public sealed class PakReloadCoordinator<TOpened> where TOpened : class, IDisposable
{
    private readonly IPakReloadHost<TOpened> _host;
    private readonly ILogger _logger;

    /// <summary>A reload is in flight, including while one of its prompts is open (a modal prompt still lets the debounce timer tick).</summary>
    private bool _running;

    /// <summary>A reload was asked for while other work held the window busy.</summary>
    private bool _pending;

    public PakReloadCoordinator(IPakReloadHost<TOpened> host, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logger);
        _host = host;
        _logger = logger;
    }

    /// <summary>Runs a reload that was skipped because the window was busy, once it no longer is.</summary>
    public async Task RunPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!_pending || _running || _host.IsBusy) return;

        await ReloadAsync(cancellationToken);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _host.CancelScheduledReload();
        if (_running) return; // a prompt or reopen of the reload in flight is still open
        if (_host.Workspace is not { } workspace) return; // nothing open yet - new settings apply automatically the next time something opens
        if (_host.IsBusy)
        {
            _pending = true; // run by RunPendingAsync when the busy work ends
            return;
        }

        _running = true;
        _pending = false;
        try
        {
            await ReloadCoreAsync(workspace, cancellationToken);
        }
        finally
        {
            _running = false;
        }
    }

    private async Task ReloadCoreAsync(AssetWorkspace workspace, CancellationToken cancellationToken)
    {
        var settings = _host.Settings;
        var openPak = _host.OpenPak;
        var decision = openPak == null ? null : PakReloadDecision.For(openPak.Format, GameProfile.For(settings.Game), settings.AesKeyHex);
        var reopenFormat = decision?.ReopenAs;
        var versionSettingsChanged = VersionSettingsChanged(settings);
        if (reopenFormat == null && !versionSettingsChanged)
        {
            if (decision?.KeyProblem is { } onlyKeyProblem)
                _host.StatusMessage = $"{onlyKeyProblem} The open pak keeps its previous key.";
            return;
        }

        _host.IsBusy = true;
        _host.StatusMessage = "Reloading with updated settings...";
        TOpened? reopened = null;
        try
        {
            // Built once, outside the reopen, so a usmap that won't load is reported as that and nothing is attempted without it.
            ResolverBuild build;
            try
            {
                build = _host.BuildResolver(settings);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Loading the usmap '{UsmapPath}' failed.", settings.UsmapPath);
                _host.StatusMessage = $"Could not load the usmap ({ex.Message}); the new settings were not applied.";
                return;
            }

            var notes = new List<string>();
            if (reopenFormat != null)
                reopened = await TryReopenAsync(openPak!, reopenFormat, build, notes, cancellationToken);

            var changed = false;
            if (reopened != null || versionSettingsChanged)
            {
                // Reopening and applying the version both drop the open assets' unsaved edits.
                if (!_host.ConfirmDiscardUnsavedEdits())
                {
                    _host.StatusMessage = "Reload skipped - your edits were kept.";
                    return;
                }

                if (reopened != null && !_host.ConfirmDiscardUnrepackedEdits())
                {
                    reopened.Dispose();
                    reopened = null;
                    notes.Add("Reopen skipped - the pak stays open with its unrepacked assets.");
                }
            }

            if (reopened != null)
            {
                var toAdopt = reopened;
                reopened = null; // the host owns it from here
                await _host.AdoptPakAsync(toAdopt);
                notes.Add("Reopened the pak with the new key/game.");
                changed = true;
            }
            else if (versionSettingsChanged)
            {
                ApplyVersionSettings(workspace, build);
                notes.Add("Applied the new engine version/usmap.");
                changed = true;
            }

            if (decision?.KeyProblem is { } keyProblem)
                notes.Add($"{keyProblem} The pak keeps its previous key.");

            if (changed)
                await _host.RefreshOpenContentAsync();
            _host.StatusMessage = string.Join(' ', notes);
        }
        catch (OperationCanceledException)
        {
            _host.StatusMessage = "Reload cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reload with updated settings failed.");
            _host.StatusMessage = $"Reload failed: {ex.Message}";
        }
        finally
        {
            reopened?.Dispose();
            _host.IsBusy = false;
        }
    }

    /// <summary>Whether the open workspace's resolver no longer matches the settings, or its usmap was missing when it was built and the file exists now.</summary>
    private bool VersionSettingsChanged(ReloadSettings settings)
    {
        var wanted = ResolverSettings.Create(settings.Version, settings.Game, settings.UsmapPath, mappingsLoaded: false);
        if (_host.AppliedSettings is not { } applied || !applied.SameInputs(wanted)) return true;

        return !applied.MappingsLoaded && wanted.UsmapPath is { } usmapPath && _host.UsmapFileExists(usmapPath);
    }

    /// <summary>Opens the pak in the new format; a failure is noted and leaves the current one as it is.</summary>
    private async Task<TOpened?> TryReopenAsync(OpenPakInfo openPak, PakFormat format, ResolverBuild build, List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            return await _host.OpenPakAsync(openPak.PakPath, format, build, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reopening '{PakPath}' with the updated key/game failed.", openPak.PakPath);
            notes.Add($"Could not reopen the pak with the new key/game ({ex.Message}); it stays open with the previous one.");
            return null;
        }
    }

    /// <summary>Swaps the resolver into <paramref name="workspace"/>; dropping its open assets drops their unsaved edits and orphans any Browse tree node expanded under them.</summary>
    private void ApplyVersionSettings(AssetWorkspace workspace, ResolverBuild build)
    {
        var droppedAssets = workspace.OpenAssetPaths;
        workspace.UpdateVersionResolver(build.Resolver);
        _host.AppliedSettings = build.Applied;
        _host.ClearUnsavedEditTracking();
        _host.ResetTreeAssets(droppedAssets);
    }
}
