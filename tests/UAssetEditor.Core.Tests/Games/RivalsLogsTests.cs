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
