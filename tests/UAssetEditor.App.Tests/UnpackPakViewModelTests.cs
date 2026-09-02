using UAssetEditor.App.ViewModels;

namespace UAssetEditor.App.Tests;

public class UnpackPakViewModelTests
{
    [Fact]
    public void CanRun_RequiresBothSourcePakAndDestinationFolder()
    {
        using var viewModel = new UnpackPakViewModel(initialSourcePakPath: null, initialAesKeyHex: "");
        Assert.False(viewModel.RunCommand.CanExecute(null));

        viewModel.SourcePakPath = @"C:\Mods\SB.pak";
        Assert.False(viewModel.RunCommand.CanExecute(null));

        viewModel.DestinationFolder = @"C:\Mods\Extracted";
        Assert.True(viewModel.RunCommand.CanExecute(null));
    }

    [Fact]
    public void PreFillingTheSourcePak_IsReflectedImmediately()
    {
        using var viewModel = new UnpackPakViewModel(initialSourcePakPath: @"C:\Mods\SB.pak", initialAesKeyHex: "abcd");

        Assert.Equal(@"C:\Mods\SB.pak", viewModel.SourcePakPath);
        Assert.Equal("abcd", viewModel.AesKeyHex);
    }
}
