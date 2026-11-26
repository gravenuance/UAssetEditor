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
        Assert.Equal(_uasset + ".bak", result.Backup);
        Assert.Equal(_uasset + ".orig.bak", result.Original);
    }

    [Fact]
    public void Result_NamesTheBackupsWhereTheyWent_InABackupFolder()
    {
        var folder = Path.Combine(_dir, "backups");

        var result = PackageWriter.WithBackup(_uasset, folder, WriteBoth);

        var backup = Path.Combine(folder, "A.uasset.bak");
        var original = Path.Combine(folder, "A.uasset.orig.bak");
        Assert.Equal(backup, result.Backup);
        Assert.Equal(original, result.Original);
        Assert.Equal("header v1", File.ReadAllText(backup));
        Assert.Equal("header v1", File.ReadAllText(original));
    }

    [Fact]
    public void Result_OfANewFile_NamesNoBackup()
    {
        var path = Path.Combine(_dir, "New.uasset");

        var result = PackageWriter.WithBackup(path, backupFolder: null, () => File.WriteAllText(path, "new"));

        Assert.Null(result.Backup);
        Assert.Null(result.Original);
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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTime Now = new(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeProvider Clock = new FixedClock(Now);
    private static readonly TimeSpan Old = TimeSpan.FromDays(2);
    private const string StagingId = "0123456789abcdef0123456789abcdef";

    private string Leftover(string name, string contents, TimeSpan age, string? folder = null)
    {
        var path = Path.Combine(folder ?? _dir, name);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    private BackupResult Save(string? backupFolder = null) => PackageWriter.WithBackup(_uasset, backupFolder, WriteBoth, Clock);

    [Fact]
    public void StagedCopiesThatDuplicateABackup_AreRemovedByTheNextSave()
    {
        // A save killed before it wrote anything leaves copies of the files as they still are, which this save's .bak now holds.
        Leftover($"A.uasset.bak.{StagingId}.tmp", "header v1", Old);
        Leftover($"A.uexp.bak.{StagingId}.tmp", "exports v1", Old);

        var result = Save();

        Assert.True(result.Placed);
        Assert.Equal(["A.uasset", "A.uasset.bak", "A.uasset.orig.bak", "A.uexp", "A.uexp.bak", "A.uexp.orig.bak"], FileNames());
    }

    [Fact]
    public void AStagedOrigBakCopy_LeftByAKilledFirstSave_IsSweptOnceItDuplicatesTheOrigBak_AndACutShortOneIsKept()
    {
        // Killed after copying, before the move: the next save makes the same .orig.bak. Killed mid-copy: the copy is cut short.
        Leftover($"A.uasset.orig.bak.{StagingId}.tmp", "header v1", Old);
        var cutShort = Leftover($"A.uexp.orig.bak.{StagingId}.tmp", "expo", Old);

        var result = Save();

        Assert.True(result.Placed);
        Assert.Equal(
            ["A.uasset", "A.uasset.bak", "A.uasset.orig.bak", "A.uexp", "A.uexp.bak", "A.uexp.orig.bak", Path.GetFileName(cutShort)],
            FileNames());
        Assert.Equal("exports v1", File.ReadAllText(_uexp + ".orig.bak"));
    }

    [Fact]
    public void AStagedCopyMatchingNoFileOnDisk_IsKept_AsItMayBeTheOnlyCopy()
    {
        // What a stranded save reported to the user, or what a save killed mid-write left of the state before it.
        var stranded = Leftover($"A.uasset.bak.{StagingId}.tmp", "header v0", Old);

        Save();

        Assert.Equal("header v0", File.ReadAllText(stranded));
    }

    [Fact]
    public void ARecentStagedCopy_IsLeftAlone_SinceAnotherSaveMayStillBeUsingIt()
    {
        var recent = Leftover($"A.uasset.bak.{StagingId}.tmp", "header v1", TimeSpan.FromMinutes(59));

        Save();

        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void TheStagedCopy_IsStampedWithTheSaveTime_NotTheSourceFilesTime()
    {
        File.SetLastWriteTimeUtc(_uasset, Now - TimeSpan.FromDays(300));

        Save();

        Assert.Equal(Now, File.GetLastWriteTimeUtc(_uasset + ".bak"));
    }

    [Fact]
    public void OnlyThisFilesStagingNames_AreSwept()
    {
        string[] kept =
        [
            Leftover("A.uasset.bak.tmp", "header v1", Old),
            Leftover("A.uasset.bak.notastagingid.tmp", "header v1", Old),
            Leftover($"A.uasset.bak.{StagingId}0.tmp", "header v1", Old),
            Leftover($"B.uasset.bak.{StagingId}.tmp", "header v1", Old),
            Leftover($"B.uasset.orig.bak.{StagingId}.tmp", "header v1", Old),
        ];

        Save();

        Assert.All(kept, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void ASaveThatPlacesNoBackup_SweepsNothing()
    {
        var old = Leftover($"A.uasset.bak.{StagingId}.tmp", "header v1", Old);

        Assert.Throws<InvalidDataException>(() => PackageWriter.WithBackup(_uasset, backupFolder: null, () => throw new InvalidDataException("refused"), Clock));

        Assert.True(File.Exists(old));
    }

    [Fact]
    public void DuplicateStagedCopiesInTheBackupFolder_AreSweptToo()
    {
        var backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(backups);
        var duplicate = Leftover($"A.uasset.bak.{StagingId}.tmp", "header v1", Old, backups);
        var anotherFiles = Leftover($"A.uexp.bak.{StagingId}.tmp", "another A.uexp's exports", Old, backups);

        Save(backups);

        Assert.False(File.Exists(duplicate));
        Assert.True(File.Exists(anotherFiles));
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
