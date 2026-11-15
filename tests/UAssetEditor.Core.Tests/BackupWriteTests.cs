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
        var result = PackageWriter.WithBackup(_uasset, backupFolder: null, WriteBoth);

        Assert.Equal(["A.uasset", "A.uasset.bak", "A.uasset.orig.bak", "A.uexp", "A.uexp.bak", "A.uexp.orig.bak"], FileNames());
        Assert.Equal("header v1", File.ReadAllText(_uasset + ".bak"));
        Assert.Equal("exports v1", File.ReadAllText(_uexp + ".bak"));
        Assert.Equal("header v2", File.ReadAllText(_uasset));
        Assert.True(result.Placed);
        Assert.Empty(result.Stranded);
    }

    [Fact]
    public void FirstSave_KeepsTheVanillaFilesAsOrigBak_AndLaterSavesNeverReplaceThem()
    {
        PackageWriter.WithBackup(_uasset, backupFolder: null, WriteBoth);
        PackageWriter.WithBackup(_uasset, backupFolder: null, () =>
        {
            File.WriteAllText(_uasset, "header v3");
            File.WriteAllText(_uexp, "exports v3");
        });

        Assert.Equal("header v1", File.ReadAllText(_uasset + ".orig.bak"));
        Assert.Equal("exports v1", File.ReadAllText(_uexp + ".orig.bak"));
        Assert.Equal("header v2", File.ReadAllText(_uasset + ".bak"));
        Assert.Equal("exports v2", File.ReadAllText(_uexp + ".bak"));
        Assert.Equal("header v3", File.ReadAllText(_uasset));
    }

    [Fact]
    public void FileSavedBeforeOrigBakExisted_GetsItsOlderBakAsTheOrigBak()
    {
        // Saves before .orig.bak existed left only a .bak: it holds an earlier state than the file about to be saved over.
        File.WriteAllText(_uasset + ".bak", "header v0");
        File.WriteAllText(_uexp + ".bak", "exports v0");

        PackageWriter.WithBackup(_uasset, backupFolder: null, WriteBoth);

        Assert.Equal("header v0", File.ReadAllText(_uasset + ".orig.bak"));
        Assert.Equal("exports v0", File.ReadAllText(_uexp + ".orig.bak"));
        Assert.Equal("header v1", File.ReadAllText(_uasset + ".bak"));
    }

    [Fact]
    public void RefusedFirstWrite_LeavesNoOrigBak_SoTheNextSaveKeepsTheRealVanillaFile()
    {
        Assert.Throws<InvalidDataException>(() => PackageWriter.WithBackup(_uasset, backupFolder: null, () => throw new InvalidDataException("refused")));
        PackageWriter.WithBackup(_uasset, backupFolder: null, WriteBoth);

        Assert.Equal("header v1", File.ReadAllText(_uasset + ".orig.bak"));
    }

    [Fact]
    public void ExistingOrigBak_IsLeftAloneEvenWhenItIsReadOnly()
    {
        var orig = _uasset + ".orig.bak";
        File.WriteAllText(orig, "header v0");
        File.SetAttributes(orig, FileAttributes.ReadOnly);
        try
        {
            var result = PackageWriter.WithBackup(_uasset, backupFolder: null, WriteBoth);

            Assert.Equal("header v0", File.ReadAllText(orig));
            Assert.True(result.Placed);
        }
        finally
        {
            File.SetAttributes(orig, FileAttributes.Normal);
        }
    }

    [Fact]
    public void StagingNameIsUnique_SoAnotherSaversStagedCopyIsNotClobbered()
    {
        var other = _uasset + ".bak.tmp";
        File.WriteAllText(other, "someone else's staged copy");

        PackageWriter.WithBackup(_uasset, backupFolder: null, WriteBoth);

        Assert.Equal("someone else's staged copy", File.ReadAllText(other));
        Assert.Equal("header v1", File.ReadAllText(_uasset + ".bak"));
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
        Assert.Equal("header v1", File.ReadAllText(_uasset + ".orig.bak"));
    }

    [Fact]
    public void BackupThatCannotBeReplaced_DoesNotFailASuccessfulWrite_ButSaysTheBackupIsNotInPlace()
    {
        var oldBackup = _uasset + ".bak";
        File.WriteAllText(oldBackup, "header v0");
        File.SetAttributes(oldBackup, FileAttributes.ReadOnly);
        try
        {
            var result = PackageWriter.WithBackup(_uasset, backupFolder: null, WriteBoth);

            Assert.Equal("header v2", File.ReadAllText(_uasset));
            Assert.Equal("header v0", File.ReadAllText(oldBackup));
            Assert.Equal("exports v1", File.ReadAllText(_uexp + ".bak"));
            Assert.False(result.Placed);
            var stranded = Assert.Single(result.Stranded); // the fresh backup waits beside the stuck one
            Assert.Equal("header v1", File.ReadAllText(stranded));
            Assert.EndsWith(".tmp", stranded, StringComparison.Ordinal);
            Assert.Equal("header v0", File.ReadAllText(_uasset + ".orig.bak")); // the older .bak, copied with its read-only flag
        }
        finally
        {
            File.SetAttributes(oldBackup, FileAttributes.Normal);
            if (File.Exists(_uasset + ".orig.bak")) File.SetAttributes(_uasset + ".orig.bak", FileAttributes.Normal);
        }
    }

    [Fact]
    public void BackupFolder_ReceivesTheBackups()
    {
        var backups = Path.Combine(_dir, "backups");

        PackageWriter.WithBackup(_uasset, backups, WriteBoth);

        Assert.Equal("header v1", File.ReadAllText(Path.Combine(backups, "A.uasset.bak")));
        Assert.Equal("exports v1", File.ReadAllText(Path.Combine(backups, "A.uexp.bak")));
        Assert.Equal("header v1", File.ReadAllText(Path.Combine(backups, "A.uasset.orig.bak")));
        Assert.Equal("exports v1", File.ReadAllText(Path.Combine(backups, "A.uexp.orig.bak")));
        Assert.Empty(Directory.GetFiles(backups, "*.tmp"));
    }
}
