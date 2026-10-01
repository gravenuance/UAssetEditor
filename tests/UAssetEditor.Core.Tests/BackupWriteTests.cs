using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

/// <summary>The backup around a package write: real files, with the write itself stood in for.</summary>
public sealed class BackupWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Backup_" + Guid.NewGuid());
    private readonly string _uasset;
    private readonly string _uexp;

    public BackupWriteTests()
    {
        Directory.CreateDirectory(_dir);
        _uasset = Path.Combine(_dir, "A.uasset");
        _uexp = Path.Combine(_dir, "A.uexp");
        File.WriteAllText(_uasset, "header v1");
        File.WriteAllText(_uexp, "exports v1");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteBoth()
    {
        File.WriteAllText(_uasset, "header v2");
        File.WriteAllText(_uexp, "exports v2");
    }

    private string[] FileNames() => [.. Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    [Fact]
    public void SuccessfulWrite_BacksUpBothFilesAsTheyWereBefore()
    {
        PackageWriter.WithBackup(_uasset, backupFolder: null, WriteBoth);

        Assert.Equal(["A.uasset", "A.uasset.bak", "A.uexp", "A.uexp.bak"], FileNames());
        Assert.Equal("header v1", File.ReadAllText(_uasset + ".bak"));
        Assert.Equal("exports v1", File.ReadAllText(_uexp + ".bak"));
        Assert.Equal("header v2", File.ReadAllText(_uasset));
    }

    [Fact]
    public void RefusedWrite_LeavesNoBackupBehind()
    {
        Assert.Throws<InvalidDataException>(() => PackageWriter.WithBackup(_uasset, backupFolder: null, () => throw new InvalidDataException("refused")));

        Assert.Equal(["A.uasset", "A.uexp"], FileNames());
    }

    [Fact]
    public void RefusedWrite_KeepsAnEarlierBackupUnchanged()
    {
        File.WriteAllText(_uasset + ".bak", "header v0");

        Assert.Throws<InvalidDataException>(() => PackageWriter.WithBackup(_uasset, backupFolder: null, () => throw new InvalidDataException("refused")));

        Assert.Equal("header v0", File.ReadAllText(_uasset + ".bak"));
        Assert.Equal(["A.uasset", "A.uasset.bak", "A.uexp"], FileNames());
    }

    [Fact]
    public void WriteFailingHalfWay_KeepsTheBackupOfTheOriginal()
    {
        Assert.Throws<IOException>(() => PackageWriter.WithBackup(_uasset, backupFolder: null, () =>
        {
            File.WriteAllText(_uasset, "header v2");
            throw new IOException("disk full");
        }));

        Assert.Equal("header v1", File.ReadAllText(_uasset + ".bak"));
        Assert.Equal("exports v1", File.ReadAllText(_uexp + ".bak"));
    }

    [Fact]
    public void BackupThatCannotBeReplaced_DoesNotFailASuccessfulWrite()
    {
        var oldBackup = _uasset + ".bak";
        File.WriteAllText(oldBackup, "header v0");
        File.SetAttributes(oldBackup, FileAttributes.ReadOnly);
        try
        {
            PackageWriter.WithBackup(_uasset, backupFolder: null, WriteBoth);

            Assert.Equal("header v2", File.ReadAllText(_uasset));
            Assert.Equal("header v1", File.ReadAllText(oldBackup + ".tmp")); // the fresh backup waits beside the stuck one
            Assert.Equal("exports v1", File.ReadAllText(_uexp + ".bak"));
        }
        finally
        {
            File.SetAttributes(oldBackup, FileAttributes.Normal);
        }
    }

    [Fact]
    public void BackupFolder_ReceivesTheBackups()
    {
        var backups = Path.Combine(_dir, "backups");

        PackageWriter.WithBackup(_uasset, backups, WriteBoth);

        Assert.Equal("header v1", File.ReadAllText(Path.Combine(backups, "A.uasset.bak")));
        Assert.Equal("exports v1", File.ReadAllText(Path.Combine(backups, "A.uexp.bak")));
        Assert.Empty(Directory.GetFiles(backups, "*.tmp"));
    }
}
