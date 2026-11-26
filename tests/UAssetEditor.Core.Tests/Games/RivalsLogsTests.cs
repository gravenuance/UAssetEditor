using UAssetEditor.Core.Games.Rivals;
using static UAssetEditor.Core.Tests.Games.RivalsLogTestData;

namespace UAssetEditor.Core.Tests.Games;

public sealed class RivalsLogsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_RivalsLogs_" + Guid.NewGuid());

    public RivalsLogsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteLog(string name, DateTime writtenUtc, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void ReadAll_ALogOverTheSizeLimit_IsRefusedBeforeItIsRead()
    {
        var path = WriteLog("big.log", DateTime.UtcNow, new byte[101]);

        var error = Assert.Throws<InvalidDataException>(() => RivalsLogs.ReadAll(path, maxBytes: 100));

        Assert.Equal($"{path} is 101 bytes; a game log over 100 bytes is not read.", error.Message);
        Assert.Equal(100, RivalsLogs.ReadAll(WriteLog("fits.log", DateTime.UtcNow, new byte[100]), maxBytes: 100).Length);
    }

    // What a FileStream gives when the game truncates or rotates the log between the size check and the read.
    private sealed class ShrunkStream(byte[] left, long lengthBefore) : MemoryStream(left)
    {
        public override long Length => lengthBefore;
    }

    [Fact]
    public void ReadAll_ALogThatShrinksBeforeItIsRead_GivesWhatIsLeft_InsteadOfFailing()
    {
        using var stream = new ShrunkStream([1, 2, 3, 4], lengthBefore: 10);

        Assert.Equal([1, 2, 3, 4], RivalsLogs.ReadAll(stream, "game.log", maxBytes: 100));
    }

    [Fact]
    public void InstalledBuild_ComesFromTheNewestLog_NotTheLastInTheAlphabet()
    {
        var now = DateTime.UtcNow;
        WriteLog("zzz-older.log", now.AddHours(-2), Log(Frame(EngineLine3939753Cipher)));
        WriteLog("aaa-newer.log", now.AddHours(-1), Log(Frame(SecondCipher), Frame(EngineLine4008108Cipher)));

        Assert.Equal(4008108, RivalsLogs.InstalledBuild(_dir));
    }

    [Fact]
    public void Newest_IgnoresOtherFiles()
    {
        var now = DateTime.UtcNow;
        var log = WriteLog("a.log", now.AddHours(-1), Log());
        WriteLog("b.txt", now, []);

        Assert.Equal(log, RivalsLogs.Newest(_dir));
    }

    [Fact]
    public void InstalledBuild_WithNoLogs_IsUnknown()
    {
        Assert.Null(RivalsLogs.InstalledBuild(_dir));
        Assert.Null(RivalsLogs.InstalledBuild(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void InstalledBuild_ALogWithoutAnEngineLine_IsUnknown()
    {
        WriteLog("a.log", DateTime.UtcNow, Log(Frame(TwoLinesCipher)));

        Assert.Null(RivalsLogs.InstalledBuild(_dir));
    }

    [Fact]
    public void InstalledBuild_AnUndecodableLog_IsUnknownAndDoesNotThrow()
    {
        WriteLog("a.log", DateTime.UtcNow, "not a game log"u8.ToArray());

        Assert.Null(RivalsLogs.InstalledBuild(_dir));
    }

    [Fact]
    public void InstalledBuild_ReadsALogTheGameStillHasOpenForWriting()
    {
        var path = WriteLog("a.log", DateTime.UtcNow, Log(Frame(EngineLine4008108Cipher)));
        using var gameHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        Assert.Equal(4008108, RivalsLogs.InstalledBuild(_dir));
    }
}
