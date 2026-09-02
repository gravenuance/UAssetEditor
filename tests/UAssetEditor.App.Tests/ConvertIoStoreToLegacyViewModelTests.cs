using UAssetEditor.App.ViewModels;

namespace UAssetEditor.App.Tests;

public class ConvertIoStoreToLegacyViewModelTests
{
    [Fact]
    public void CanRun_RequiresBothSourceUtocAndOutputPath()
    {
        using var viewModel = new ConvertIoStoreToLegacyViewModel(initialSourceUtocPath: null, initialAesKeyHex: "");
        Assert.False(viewModel.RunCommand.CanExecute(null));

        viewModel.SourceUtocPath = @"C:\Mods\SB.utoc";
        Assert.False(viewModel.RunCommand.CanExecute(null));

        viewModel.OutputPath = @"C:\Mods\Extracted";
        Assert.True(viewModel.RunCommand.CanExecute(null));
    }

    [Fact]
    public void ChangingOutputFormat_ClearsThePreviouslyChosenOutputPath()
    {
        // The two formats want different kinds of path (a folder vs. a .pak file) - keeping a
        // stale folder path around after switching to Pak (or vice versa) would silently try
        // to convert into the wrong kind of target.
        using var viewModel = new ConvertIoStoreToLegacyViewModel(initialSourceUtocPath: null, initialAesKeyHex: "")
        {
            OutputPath = @"C:\Mods\Extracted",
        };

        viewModel.OutputFormat = LegacyOutputFormat.Pak;

        Assert.Equal("", viewModel.OutputPath);
    }
}
