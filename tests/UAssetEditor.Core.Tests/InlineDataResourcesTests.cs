using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

public class InlineDataResourcesTests
{
    private static FObjectDataResource Resource(EObjectDataResourceFlags flags, long offset, long size, int export = 0, uint legacy = 0) =>
        new(flags, offset, -1, size, size, FPackageIndex.FromExport(export), legacy);

    private static readonly ExportTail Before = new(Start: 300, Length: 3000);
    private static readonly ExportTail After = new(Start: 158, Length: 3000);

    [Fact]
    public void Shift_MovesAnInlineResourceByItsExportsMove()
    {
        List<FObjectDataResource> resources = [Resource(EObjectDataResourceFlags.Inline, 335, 2048)];

        var moved = InlineDataResources.Shift(resources, [Before], [After]);

        Assert.Equal(1, moved);
        Assert.Equal(193, resources[0].SerialOffset);
    }

    [Fact]
    public void Shift_MovesAResourceMarkedInlineOnlyByItsLegacyBulkFlags()
    {
        // What a UE 5.3 cook writes for a texture's small mips: SingleUse | ForceInlinePayload, no new-style flag.
        List<FObjectDataResource> resources = [Resource(EObjectDataResourceFlags.None, 335, 2048, legacy: 0x48)];

        InlineDataResources.Shift(resources, [Before], [After]);

        Assert.Equal(193, resources[0].SerialOffset);
    }

    [Fact]
    public void Shift_LeavesALegacyFlaggedSeparateFilePayloadAlone()
    {
        // PayloadAtEndOfFile | PayloadInSeperateFile | Force_NOT_InlinePayload | NoOffsetFixUp: the big mips in .ubulk.
        List<FObjectDataResource> resources = [Resource(EObjectDataResourceFlags.None, 0x80000, 131072, legacy: 0x10501)];

        InlineDataResources.Shift(resources, [Before], [After]);

        Assert.Equal(0x80000, resources[0].SerialOffset);
    }

    [Fact]
    public void Shift_LeavesResourcesInSeparateFilesAlone()
    {
        List<FObjectDataResource> resources = [Resource(EObjectDataResourceFlags.Streaming, 0x80000, 131072)];

        InlineDataResources.Shift(resources, [Before], [After]);

        Assert.Equal(0x80000, resources[0].SerialOffset);
    }

    [Fact]
    public void Shift_UsesTheMoveOfTheResourcesOwnExport()
    {
        List<FObjectDataResource> resources = [Resource(EObjectDataResourceFlags.Inline, 5100, 16, export: 1)];

        InlineDataResources.Shift(resources, [Before, new ExportTail(5000, 200)], [After, new ExportTail(4900, 200)]);

        Assert.Equal(5000, resources[0].SerialOffset);
    }

    [Fact]
    public void Shift_ChangesNothing_WhenNoExportMoved()
    {
        List<FObjectDataResource> resources = [Resource(EObjectDataResourceFlags.Inline, 335, 2048)];

        var moved = InlineDataResources.Shift(resources, [Before], [Before]);

        Assert.Equal(0, moved);
        Assert.Equal(335, resources[0].SerialOffset);
    }

    [Fact]
    public void Shift_RefusesAnInlineResourceOutsideItsExportsTail_WhenThatExportMoved()
    {
        List<FObjectDataResource> resources = [Resource(EObjectDataResourceFlags.Inline, 10, 2048)];

        Assert.Throws<InvalidOperationException>(() => InlineDataResources.Shift(resources, [Before], [After]));
    }

    [Fact]
    public void Shift_RefusesATailWhoseLengthChanged()
    {
        List<FObjectDataResource> resources = [Resource(EObjectDataResourceFlags.Inline, 335, 2048)];

        Assert.Throws<InvalidOperationException>(() => InlineDataResources.Shift(resources, [Before], [new ExportTail(158, 2900)]));
    }
}
