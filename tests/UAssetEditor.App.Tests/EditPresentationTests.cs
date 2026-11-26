using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.Editing;

namespace UAssetEditor.App.Tests;

public class EditPresentationTests
{
    private static readonly PropertyChange Edit = new("a.uasset", 0, "Export", "Count", "SetValue", "5", "9");

    [Fact]
    public void Failures_AfterAnApply_SaysAFailedAssetKeepsTheEditsMadeBeforeItsFailure()
    {
        AssetChangeSet[] changeSets = [new("dir/a.uasset", [Edit], "regular expression timed out after 3 s")];

        Assert.Equal(
            " 1 asset(s) failed, first a.uasset: regular expression timed out after 3 s. Edits made before a failure are kept unsaved; Revert drops them.",
            EditPresentation.Failures(changeSets, editsKept: true));
    }

    [Fact]
    public void Failures_AfterAPreview_OrWithNoEditsMade_SaysNothingIsKept()
    {
        AssetChangeSet[] partial = [new("a.uasset", [Edit], "boom")];
        AssetChangeSet[] untouched = [new("a.uasset", [], "boom")];

        Assert.Equal(" 1 asset(s) failed, first a.uasset: boom.", EditPresentation.Failures(partial, editsKept: false));
        Assert.Equal(" 1 asset(s) failed, first a.uasset: boom.", EditPresentation.Failures(untouched, editsKept: true));
    }

    [Fact]
    public void Failures_CountsSkippedAssets_AndIsEmptyWhenAllWent()
    {
        AssetChangeSet[] skipped = [new("a.uasset", [], "cannot open", Skipped: true), new("b.uasset", [Edit])];

        Assert.Equal(" 1 unreadable asset(s) skipped.", EditPresentation.Failures(skipped, editsKept: true));
        Assert.Equal("", EditPresentation.Failures([new("b.uasset", [Edit])], editsKept: true));
    }
}
