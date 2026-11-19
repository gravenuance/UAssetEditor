using System.IO;
using UAssetAPI.UnrealTypes;
using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;

namespace UAssetEditor.App.Tests;

/// <summary>
/// Covers PackFolderViewModel's pure logic - mount-point auto-guessing, the computed
/// format-toggle properties, and CanRun's validation - without touching BrowseSource/
/// BrowseOutput/DetectMountPoint, which open real OS file dialogs and can't run headless.
/// </summary>
public class PackFolderViewModelTests
{
    private static PackFolderViewModel CreateViewModel(bool mountPointIsAuthoritative = false) =>
        new(initialSourceFolder: null, initialMountPoint: "../../../Game/", initialAesKeyHex: "",
            defaultEngineVersion: EngineVersion.VER_UE5_3, mountPointIsAuthoritative: mountPointIsAuthoritative);

    [Fact]
    public void SettingSourceFolder_GuessesMountPointFromTheFolderName()
    {
        using var viewModel = CreateViewModel();

        viewModel.SourceFolder = @"C:\Mods\SB";

        Assert.Equal("../../../SB/", viewModel.MountPoint);
    }

    [Fact]
    public void SettingSourceFolder_DoesNotOverwriteAnAuthoritativeMountPoint()
    {
        // Mirrors how MainViewModel.PackFolder() constructs this ViewModel when a real pak is
        // already open - the mount point it read from that pak must survive the folder guess.
        using var viewModel = CreateViewModel(mountPointIsAuthoritative: true);

        viewModel.SourceFolder = @"C:\Mods\SomeUnrelatedFolderName";

        Assert.Equal("../../../Game/", viewModel.MountPoint);
    }

    [Fact]
    public void TypingAMountPointByHand_MakesItAuthoritativeGoingForward()
    {
        using var viewModel = CreateViewModel();

        viewModel.MountPoint = "../../../BendGame/";
        viewModel.SourceFolder = @"C:\Mods\SB";

        // The hand-typed value must stick even though a folder was picked afterward.
        Assert.Equal("../../../BendGame/", viewModel.MountPoint);
    }

    [Theory]
    [InlineData(PackOutputFormat.Pak, true, false)]
    [InlineData(PackOutputFormat.IoStore, false, true)]
    public void OutputFormat_DrivesTheFormatToggleProperties(PackOutputFormat format, bool expectedIsPak, bool expectedIsIoStore)
    {
        using var viewModel = CreateViewModel();

        viewModel.OutputFormat = format;

        Assert.Equal(expectedIsPak, viewModel.IsPakFormat);
        Assert.Equal(expectedIsIoStore, viewModel.IsIoStoreFormat);
    }

    [Fact]
    public async Task RunningAPakPack_ForMarvelRivals_WritesThePakInTheGamesFormat()
    {
        var rivals = GameProfile.For(Game.MarvelRivals)!;
        var folder = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_PackVm_" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "A.txt"), "a", TestContext.Current.CancellationToken);
        var output = folder + ".pak";
        try
        {
            using var viewModel = new PackFolderViewModel(folder, "../../../Mod/", rivals.AesKeyHex, EngineVersion.VER_UE5_3, game: Game.MarvelRivals) { OutputPath = output };

            await viewModel.RunCommand.ExecuteAsync(null);

            var readAsRivals = await Record.ExceptionAsync(() => Task.Run(() => PakMountPointReader.Read(output, PakAesKey.Parse(rivals.AesKeyHex), rivals.PakVariant)));
            Assert.Null(readAsRivals);
            Assert.True(viewModel.IsDone, viewModel.Status);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void CanRun_RequiresBothSourceFolderAndOutputPath()
    {
        using var viewModel = CreateViewModel();
        Assert.False(viewModel.RunCommand.CanExecute(null));

        viewModel.SourceFolder = @"C:\Mods\SB";
        Assert.False(viewModel.RunCommand.CanExecute(null));

        viewModel.OutputPath = @"C:\Mods\SB.pak";
        Assert.True(viewModel.RunCommand.CanExecute(null));
    }
}
