using System.Text;
using ModelContextProtocol;
using UAssetEditor.Core.Tests;
using UAssetEditor.Mcp.Tests.Rig;
using UAssetEditor.Mcp.Tools;

namespace UAssetEditor.Mcp.Tests;

// The pak and IoStore tools run the bundled pak worker and retoc, which are Windows builds, as the Core pak tests do.
public sealed class ArchiveReadToolTests : IDisposable
{
    private readonly McpRig _rig = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _rig.Dispose();

    private string MakePak(string name, params string[] entries)
    {
        var temp = TestPaks.CreatePak(entries.ToDictionary(e => e, e => Encoding.UTF8.GetBytes(e)));
        var path = _rig.Folder.Combine(name);
        File.Move(temp, path);
        return path;
    }

    [Fact]
    public async Task PakInfo_ReadsTheMountPointAndVersion()
    {
        var pak = MakePak("a.pak", "Content/A.txt");

        var result = await _rig.ArchiveReads.PakInfoAsync(pak, cancellationToken: _ct);

        Assert.Equal(pak, result.Pak);
        Assert.Equal("../../../Game/", result.MountPoint);
        Assert.Equal("V11", result.Version);
    }

    [Fact]
    public async Task PakInfo_NeverEchoesTheKeyItWasGiven_InAResultOrAnError()
    {
        var pak = MakePak("a.pak", "Content/A.txt");
        const string key = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";
        PakInfoResult? result = null;

        // The pak is not encrypted, so the worker may take the key or refuse it; either way nothing may repeat it.
        var error = await Record.ExceptionAsync(async () => result = await _rig.ArchiveReads.PakInfoAsync(pak, aesKey: key, cancellationToken: _ct));

        var shown = error?.Message ?? System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain(key, shown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PakInfo_RefusesAMissingFileAFileOfAnotherKindAndABadKey()
    {
        var pak = MakePak("a.pak", "Content/A.txt");
        var text = _rig.Folder.Combine("a.txt");
        await File.WriteAllTextAsync(text, "x", _ct);

        var missing = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveReads.PakInfoAsync(_rig.Folder.Combine("no.pak"), cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveReads.PakInfoAsync(text, cancellationToken: _ct));
        var key = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveReads.PakInfoAsync(pak, aesKey: "not hex!", cancellationToken: _ct));

        Assert.Contains("not found", missing.Message, StringComparison.Ordinal);
        Assert.Contains("hex", key.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PakInfo_WhenCancelled_Throws()
    {
        var pak = MakePak("a.pak", "Content/A.txt");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.ArchiveReads.PakInfoAsync(pak, cancellationToken: McpRig.Cancelled));
    }

    [Fact]
    public async Task PakList_ListsEntriesInOrder_MarkingTheAssets()
    {
        var pak = MakePak("a.pak", "Content/B.txt", "Content/A.uexp", "Content/A.uasset");

        var result = await _rig.ArchiveReads.PakListAsync(pak, cancellationToken: _ct);

        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Matched);
        Assert.Equal(["Content/A.uasset", "Content/A.uexp", "Content/B.txt"], result.Entries.Select(e => e.Path));
        Assert.Equal([true, false, false], result.Entries.Select(e => e.IsAsset));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task PakList_WithAFilter_KeepsTheMatches_IgnoringCase()
    {
        var pak = MakePak("a.pak", "Content/B.txt", "Content/A.uasset", "Other/C.txt");

        var result = await _rig.ArchiveReads.PakListAsync(pak, filter: "content/", cancellationToken: _ct);

        Assert.Equal(3, result.Total);
        Assert.Equal(["Content/A.uasset", "Content/B.txt"], result.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task PakList_CapsTheEntriesAndSaysHowManyMatched()
    {
        var pak = MakePak("a.pak", "Content/A.txt", "Content/B.txt", "Content/C.txt");

        var result = await _rig.ArchiveReads.PakListAsync(pak, maxResults: 2, cancellationToken: _ct);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(3, result.Matched);
        Assert.True(result.Truncated);
        Assert.Contains("first 2 of 3", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PakList_RefusesAMaxResultsOutsideTheRange_AndHonoursCancellation()
    {
        var pak = MakePak("a.pak", "Content/A.txt");

        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveReads.PakListAsync(pak, maxResults: 0, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveReads.PakListAsync(pak, maxResults: 5001, cancellationToken: _ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.ArchiveReads.PakListAsync(pak, cancellationToken: McpRig.Cancelled));
    }

    [Fact]
    public async Task IoStoreList_ListsAContainerPackedFromAFolder_AndRefusesBadInput()
    {
        var source = _rig.Folder.Combine("src", "Content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "Foo.txt"), "hello", _ct);
        var utoc = _rig.Folder.Combine("out", "mod.utoc");
        await _rig.ArchiveWrites.PackAsync(_rig.Folder.Combine("src"), utoc, engineVersion: "VER_UE5_3", cancellationToken: _ct);

        var result = await _rig.ArchiveReads.IoStoreListAsync(utoc, cancellationToken: _ct);

        // A container of plain files has no package paths to list; the call must still succeed.
        Assert.Equal(utoc, result.Container);
        Assert.False(result.Truncated);
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveReads.IoStoreListAsync(_rig.Folder.Combine("no.utoc"), cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveReads.IoStoreListAsync(utoc, maxResults: 0, cancellationToken: _ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.ArchiveReads.IoStoreListAsync(utoc, cancellationToken: McpRig.Cancelled));
    }

    [Fact]
    public void TheWindowsOnlyGuard_PassesOnWindows_AndExplainsItselfElsewhere()
    {
        var error = Record.Exception(() => ToolRuntime.RequireWindows("pak_list"));

        if (OperatingSystem.IsWindows())
        {
            Assert.Null(error);
        }
        else
        {
            var mcp = Assert.IsType<McpException>(error);
            Assert.Contains("pak_list needs Windows", mcp.Message, StringComparison.Ordinal);
        }
    }
}
