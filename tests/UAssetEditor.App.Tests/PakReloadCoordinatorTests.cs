using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.App.Tests;

public sealed class PakReloadCoordinatorTests
{
    private static readonly PakFormat OldFormat = PakFormat.Of(null, PakVariant.Standard);

    // A key that differs from OldFormat's (no key), so the open pak has to be reopened.
    private const string NewKeyHex = "0101010101010101010101010101010101010101010101010101010101010101";

    private static readonly ReloadSettings Unchanged = new(EngineVersion.VER_UE5_3, Game.None, null, "");

    private sealed class FakeOpened(OpenPakInfo info, AssetWorkspace workspace, ResolverSettings applied) : IDisposable
    {
        public OpenPakInfo Info { get; } = info;
        public AssetWorkspace Workspace { get; } = workspace;
        public ResolverSettings Applied { get; } = applied;
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSource : IAssetSource
    {
        public IEnumerable<string> EnumerateAssetPaths() => [];

        public UAsset OpenAsset(string assetPath, EngineVersion engineVersion, Usmap? mappings, Game game) => new(engineVersion);

        public void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder) { }
    }

    private sealed class FakeHost : IPakReloadHost<FakeOpened>
    {
        public FakeHost(ReloadSettings applied, bool pakBacked = true, params string[] openAssets)
        {
            Workspace = NewWorkspace(applied);
            foreach (var asset in openAssets)
                Workspace.GetOrOpen(asset);
            AppliedSettings = ResolverSettings.Create(applied.Version, applied.Game, applied.UsmapPath, mappingsLoaded: applied.UsmapPath != null);
            OpenPak = pakBacked ? new OpenPakInfo("old.pak", OldFormat) : null;
            Settings = applied;
        }

        public List<string> Calls { get; } = [];
        public List<FakeOpened> Opened { get; } = [];
        public List<string[]> TreeResets { get; } = [];
        public int ScheduledReloadCancellations { get; private set; }

        public bool IsBusy { get; set; }
        public AssetWorkspace? Workspace { get; set; }
        public OpenPakInfo? OpenPak { get; private set; }
        public ReloadSettings Settings { get; set; }
        public ResolverSettings? AppliedSettings { get; set; }
        public string StatusMessage { get; set; } = "";

        public int UnsavedEdits { get; set; }
        public int UnrepackedEdits { get; set; }
        public bool AgreeToLoseUnsaved { get; set; } = true;
        public bool AgreeToLoseUnrepacked { get; set; } = true;

        public Exception? BuildFailure { get; set; }
        public Exception? OpenFailure { get; set; }
        public bool UsmapLoads { get; set; } = true;
        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Action? OnConfirmUnsaved { get; set; }

        public int BuildCalls { get; private set; }

        public bool UsmapFileExists(string path) => ExistingFiles.Contains(path);

        public ResolverBuild BuildResolver(ReloadSettings settings)
        {
            BuildCalls++;
            if (BuildFailure != null) throw BuildFailure;
            var resolver = new EngineVersionResolver { DefaultVersion = settings.Version, Game = settings.Game };
            return new ResolverBuild(resolver, ResolverSettings.Create(settings.Version, settings.Game, settings.UsmapPath, UsmapLoads && settings.UsmapPath != null));
        }

        public Task<FakeOpened> OpenPakAsync(string pakPath, PakFormat format, ResolverBuild build, CancellationToken cancellationToken)
        {
            Calls.Add("open");
            if (OpenFailure != null) throw OpenFailure;
            var opened = new FakeOpened(new OpenPakInfo(pakPath, format), new AssetWorkspace(new FakeSource(), build.Resolver), build.Applied);
            Opened.Add(opened);
            return Task.FromResult(opened);
        }

        public bool ConfirmDiscardUnsavedEdits()
        {
            Calls.Add("confirm-unsaved");
            OnConfirmUnsaved?.Invoke();
            return UnsavedEdits == 0 || AgreeToLoseUnsaved;
        }

        public bool ConfirmDiscardUnrepackedEdits()
        {
            Calls.Add("confirm-unrepacked");
            return UnrepackedEdits == 0 || AgreeToLoseUnrepacked;
        }

        public Task AdoptPakAsync(FakeOpened opened)
        {
            Calls.Add("adopt");
            OpenPak = opened.Info;
            Workspace = opened.Workspace;
            AppliedSettings = opened.Applied;
            UnsavedEdits = 0;
            UnrepackedEdits = 0;
            return Task.CompletedTask;
        }

        public void ClearUnsavedEditTracking()
        {
            Calls.Add("clear-unsaved");
            UnsavedEdits = 0;
        }

        public void ResetTreeAssets(IReadOnlyCollection<string> assetPaths) => TreeResets.Add([.. assetPaths.Order(StringComparer.Ordinal)]);

        public Exception? RefreshFailure { get; set; }

        public Task RefreshOpenContentAsync()
        {
            Calls.Add("refresh");
            return RefreshFailure == null ? Task.CompletedTask : Task.FromException(RefreshFailure);
        }

        public void CancelScheduledReload() => ScheduledReloadCancellations++;

        private static AssetWorkspace NewWorkspace(ReloadSettings settings) =>
            new(new FakeSource(), new EngineVersionResolver { DefaultVersion = settings.Version, Game = settings.Game });
    }

    private static PakReloadCoordinator<FakeOpened> CoordinatorFor(FakeHost host) => new(host, NullLogger.Instance);

    [Fact]
    public async Task DecliningTheUnrepackedPrompt_KeepsThePak_ButStillAppliesAChangedEngineVersion()
    {
        var host = new FakeHost(Unchanged, true, "A", "B") { UnsavedEdits = 1, UnrepackedEdits = 3, AgreeToLoseUnrepacked = false };
        var oldWorkspace = host.Workspace;
        host.Settings = new ReloadSettings(EngineVersion.VER_UE5_4, Game.None, null, NewKeyHex);

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Same(oldWorkspace, host.Workspace);
        Assert.Equal("old.pak", host.OpenPak!.PakPath);
        Assert.Equal(OldFormat, host.OpenPak.Format);
        Assert.Equal(3, host.UnrepackedEdits);
        Assert.Equal(EngineVersion.VER_UE5_4, host.AppliedSettings!.Version);
        Assert.Empty(host.Workspace!.OpenAssetPaths);
        Assert.Equal(0, host.UnsavedEdits);
    }

    [Fact]
    public async Task DecliningTheUnsavedPrompt_AppliesNothing()
    {
        var host = new FakeHost(Unchanged, true, "A") { UnsavedEdits = 2, AgreeToLoseUnsaved = false };
        host.Settings = new ReloadSettings(EngineVersion.VER_UE5_4, Game.None, null, NewKeyHex);

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EngineVersion.VER_UE5_3, host.AppliedSettings!.Version);
        Assert.Equal(["A"], host.Workspace!.OpenAssetPaths);
        Assert.Equal(2, host.UnsavedEdits);
        Assert.DoesNotContain("adopt", host.Calls);
        Assert.All(host.Opened, opened => Assert.True(opened.Disposed));
        Assert.Contains("kept", host.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUsmapThatCannotBeLoaded_IsReportedAsAUsmapProblem_AndNothingIsAttemptedWithoutIt()
    {
        var host = new FakeHost(Unchanged, true, "A") { BuildFailure = new InvalidDataException("bad magic") };
        host.Settings = new ReloadSettings(EngineVersion.VER_UE5_3, Game.None, "C:/maps/broken.usmap", NewKeyHex);

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, host.BuildCalls);
        Assert.Contains("usmap", host.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bad magic", host.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not reopen", host.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Reload failed", host.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("open", host.Calls);
        Assert.Equal(["A"], host.Workspace!.OpenAssetPaths);
        Assert.False(host.IsBusy);
    }

    [Fact]
    public async Task TheNewPakIsOpenedBeforeTheUnrepackedPrompt_AndDisposedWhenTheUserDeclines()
    {
        var host = new FakeHost(Unchanged) { UnrepackedEdits = 4, AgreeToLoseUnrepacked = false };
        host.Settings = Unchanged with { AesKeyHex = NewKeyHex };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["open", "confirm-unsaved", "confirm-unrepacked"], host.Calls);
        var opened = Assert.Single(host.Opened);
        Assert.True(opened.Disposed);
        Assert.Equal("old.pak", host.OpenPak!.PakPath);
    }

    [Fact]
    public async Task AReopenThatFails_NeverAsksAboutLosingEdits()
    {
        var host = new FakeHost(Unchanged) { UnsavedEdits = 2, UnrepackedEdits = 5, OpenFailure = new IOException("wrong key") };
        host.Settings = Unchanged with { AesKeyHex = NewKeyHex };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["open"], host.Calls);
    }

    [Fact]
    public async Task AReopenThatFails_KeepsTheOldSourceAndItsEditTracking()
    {
        var host = new FakeHost(Unchanged, true, "A") { UnsavedEdits = 2, UnrepackedEdits = 5, OpenFailure = new IOException("wrong key") };
        var oldWorkspace = host.Workspace;
        host.Settings = Unchanged with { AesKeyHex = NewKeyHex };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("adopt", host.Calls);
        Assert.Same(oldWorkspace, host.Workspace);
        Assert.Equal("old.pak", host.OpenPak!.PakPath);
        Assert.Equal(OldFormat, host.OpenPak.Format);
        Assert.Equal(["A"], host.Workspace!.OpenAssetPaths);
        Assert.Equal(2, host.UnsavedEdits);
        Assert.Equal(5, host.UnrepackedEdits);
        Assert.Contains("Could not reopen", host.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("wrong key", host.StatusMessage, StringComparison.Ordinal);
        Assert.False(host.IsBusy);
    }

    [Fact]
    public async Task AVersionOnlyChange_KeepsUnrepackedEdits_AndNeverAsksAboutThem()
    {
        var host = new FakeHost(Unchanged, true, "A") { UnsavedEdits = 1, UnrepackedEdits = 6 };
        host.Settings = Unchanged with { Version = EngineVersion.VER_UE5_4 };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("confirm-unrepacked", host.Calls);
        Assert.DoesNotContain("open", host.Calls);
        Assert.Equal(6, host.UnrepackedEdits);
        Assert.Equal(0, host.UnsavedEdits);
        Assert.Equal("old.pak", host.OpenPak!.PakPath);
        Assert.Equal(EngineVersion.VER_UE5_4, host.AppliedSettings!.Version);
    }

    [Fact]
    public async Task AReloadSkippedBecauseTheWindowIsBusy_RunsWhenTheBusyWorkFinishes()
    {
        var host = new FakeHost(Unchanged, true, "A") { IsBusy = true };
        host.Settings = Unchanged with { Version = EngineVersion.VER_UE5_4 };
        var coordinator = CoordinatorFor(host);

        await coordinator.ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EngineVersion.VER_UE5_3, host.AppliedSettings!.Version);

        await coordinator.RunPendingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EngineVersion.VER_UE5_3, host.AppliedSettings!.Version); // still busy

        host.IsBusy = false;
        await coordinator.RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EngineVersion.VER_UE5_4, host.AppliedSettings!.Version);
        Assert.Empty(host.Workspace!.OpenAssetPaths);
    }

    [Fact]
    public async Task WithNothingSkipped_RunPendingDoesNothing()
    {
        var host = new FakeHost(Unchanged);
        host.Settings = Unchanged with { Version = EngineVersion.VER_UE5_4 };

        await CoordinatorFor(host).RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EngineVersion.VER_UE5_3, host.AppliedSettings!.Version);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task AReloadWhileAPromptIsOpen_DoesNotStartAnother()
    {
        var host = new FakeHost(Unchanged, true, "A") { UnsavedEdits = 1 };
        host.Settings = Unchanged with { Version = EngineVersion.VER_UE5_4 };
        var coordinator = CoordinatorFor(host);
        Task? nested = null;
        host.OnConfirmUnsaved = () =>
        {
            host.OnConfirmUnsaved = null;
            nested = coordinator.ReloadAsync(TestContext.Current.CancellationToken); // the debounce tick that fires while the prompt is up
        };

        await coordinator.ReloadAsync(TestContext.Current.CancellationToken);
        await nested!;

        Assert.Equal(1, host.Calls.Count(c => c == "confirm-unsaved"));
        Assert.Equal(EngineVersion.VER_UE5_4, host.AppliedSettings!.Version);
    }

    [Fact]
    public async Task AReloadWhileAPromptIsOpen_IsNotQueuedToAskAgainAfterTheUserDeclined()
    {
        var host = new FakeHost(Unchanged, true, "A") { UnsavedEdits = 1, AgreeToLoseUnsaved = false };
        host.Settings = Unchanged with { Version = EngineVersion.VER_UE5_4 };
        var coordinator = CoordinatorFor(host);
        host.OnConfirmUnsaved = () =>
        {
            host.OnConfirmUnsaved = null;
            _ = coordinator.ReloadAsync(TestContext.Current.CancellationToken);
        };

        await coordinator.ReloadAsync(TestContext.Current.CancellationToken);
        await coordinator.RunPendingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, host.Calls.Count(c => c == "confirm-unsaved"));
    }

    [Fact]
    public async Task EveryReload_StopsThePendingDebounce_BeforeDoingAnythingElse()
    {
        var host = new FakeHost(Unchanged);

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, host.ScheduledReloadCancellations);
    }

    [Fact]
    public async Task ApplyingANewVersion_ResetsTheBrowseTreeNodesOfEveryAssetThatWasOpen()
    {
        var host = new FakeHost(Unchanged, false, "Game/B.uasset", "Game/A.uasset");
        host.Settings = Unchanged with { Version = EngineVersion.VER_UE5_4 };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal([["Game/A.uasset", "Game/B.uasset"]], host.TreeResets);
        Assert.Empty(host.Workspace!.OpenAssetPaths);
    }

    [Fact]
    public async Task AReopen_DoesNotResetTreeNodes_TheTreeIsRebuilt()
    {
        var host = new FakeHost(Unchanged, true, "A");
        host.Settings = Unchanged with { AesKeyHex = NewKeyHex };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["open", "confirm-unsaved", "confirm-unrepacked", "adopt", "refresh"], host.Calls);
        Assert.Empty(host.TreeResets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankUsmapPath_IsTheSameAsNone(string blank)
    {
        var host = new FakeHost(Unchanged);
        host.Settings = Unchanged with { UsmapPath = blank };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(host.Calls);
        Assert.Equal(0, host.BuildCalls);
    }

    [Fact]
    public async Task TheSameUsmapSpelledDifferently_IsNotAChange()
    {
        var full = Path.GetFullPath("maps/Game.usmap");
        var host = new FakeHost(Unchanged);
        host.AppliedSettings = ResolverSettings.Create(EngineVersion.VER_UE5_3, Game.None, full, mappingsLoaded: true);
        host.Settings = Unchanged with { UsmapPath = "  maps/GAME.usmap " };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(host.Calls);
        Assert.Equal(0, host.BuildCalls);
    }

    [Fact]
    public async Task AUsmapFileThatAppearsAfterTheLastReload_IsPickedUpOnTheNextOne()
    {
        var path = Path.GetFullPath("maps/Late.usmap");
        var host = new FakeHost(Unchanged);
        host.AppliedSettings = ResolverSettings.Create(EngineVersion.VER_UE5_3, Game.None, path, mappingsLoaded: false);
        host.Settings = Unchanged with { UsmapPath = path };
        var coordinator = CoordinatorFor(host);

        await coordinator.ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, host.BuildCalls); // still missing: nothing to apply

        host.ExistingFiles.Add(path);
        await coordinator.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, host.BuildCalls);
        Assert.True(host.AppliedSettings!.MappingsLoaded);
    }

    [Fact]
    public async Task AUsmapThatLoadedAndHasNotChanged_IsNotReloadedAgain()
    {
        var path = Path.GetFullPath("maps/Game.usmap");
        var host = new FakeHost(Unchanged);
        host.AppliedSettings = ResolverSettings.Create(EngineVersion.VER_UE5_3, Game.None, path, mappingsLoaded: true);
        host.ExistingFiles.Add(path);
        host.Settings = Unchanged with { UsmapPath = path };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, host.BuildCalls);
    }

    [Fact]
    public async Task ABadKeyText_IsReported_AndTheVersionStillApplies()
    {
        var host = new FakeHost(Unchanged);
        host.Settings = new ReloadSettings(EngineVersion.VER_UE5_4, Game.None, null, "not hex");

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EngineVersion.VER_UE5_4, host.AppliedSettings!.Version);
        Assert.Contains("not a valid hex", host.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("open", host.Calls);
    }

    [Fact]
    public async Task AFailureWhileReplayingTheContent_IsReported_AndTheWindowIsNotLeftBusy()
    {
        var host = new FakeHost(Unchanged) { RefreshFailure = new InvalidOperationException("grid gone") };
        host.Settings = Unchanged with { Version = EngineVersion.VER_UE5_4 };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Reload failed: grid gone", host.StatusMessage);
        Assert.False(host.IsBusy);
    }

    [Fact]
    public async Task WithNothingOpen_NothingHappens()
    {
        var host = new FakeHost(Unchanged) { Workspace = null };
        host.Settings = Unchanged with { Version = EngineVersion.VER_UE5_4 };

        await CoordinatorFor(host).ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(host.Calls);
        Assert.False(host.IsBusy);
    }
}
