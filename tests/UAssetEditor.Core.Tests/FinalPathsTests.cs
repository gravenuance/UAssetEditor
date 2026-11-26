using System.Runtime.InteropServices;

namespace UAssetEditor.Core.Tests;

/// <summary>Real links on a real volume: the checks are about what Windows resolves, which no fake reproduces. The link
/// kinds that need no privilege are used (junction, hard link, 8.3 short name); they are Windows features, so on another
/// system there is nothing to test.</summary>
public sealed class FinalPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_FinalPaths_" + Guid.NewGuid().ToString("N"));

    private readonly TestLinks _links = new();

    public FinalPathsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _links.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Resolve_FollowsAJunctionInTheMiddleOfAPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var real = Path.Combine(_dir, "real");
        Directory.CreateDirectory(Path.Combine(real, "sub"));
        var link = Path.Combine(_dir, "link");
        _links.MakeJunction(link, real);

        var viaLink = FinalPaths.Resolve(Path.Combine(link, "sub", "a.pak"));

        Assert.Equal(Path.Combine(FinalPaths.Resolve(real), "sub", "a.pak"), viaLink);
    }

    [Fact]
    public void Resolve_KeepsAPartThatDoesNotExistYet_AsGiven()
    {
        var resolved = FinalPaths.Resolve(Path.Combine(_dir, "not", "there", "x.pak"));

        Assert.Equal(Path.Combine(FinalPaths.Resolve(_dir), "not", "there", "x.pak"), resolved);
    }

    [Fact]
    public void Resolve_GivesAShortNameItsLongForm()
    {
        if (!OperatingSystem.IsWindows()) return;
        var longFolder = Path.Combine(_dir, "A Folder With A Long Name");
        Directory.CreateDirectory(longFolder);
        var shortFolder = TestLinks.ShortName(longFolder);

        Assert.Equal(Path.Combine(FinalPaths.Resolve(longFolder), "x.pak"), FinalPaths.Resolve(Path.Combine(shortFolder, "x.pak")));
    }

    [Fact]
    public void Resolve_GivesTheOnDiskLetterCase()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(_dir, "MixedCase");
        Directory.CreateDirectory(folder);

        Assert.Equal(FinalPaths.Resolve(folder), FinalPaths.Resolve(Path.Combine(_dir, "mixedCASE")));
    }

    [Fact]
    public void SameFile_SeesThroughAJunction()
    {
        if (!OperatingSystem.IsWindows()) return;
        var real = Path.Combine(_dir, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_dir, "link");
        _links.MakeJunction(link, real);
        File.WriteAllText(Path.Combine(real, "a.pak"), "x");

        Assert.True(FinalPaths.SameFile(Path.Combine(real, "a.pak"), Path.Combine(link, "a.pak")));
        Assert.True(FinalPaths.SameFile(Path.Combine(real, "new.pak"), Path.Combine(link, "new.pak")));
        Assert.False(FinalPaths.SameFile(Path.Combine(real, "a.pak"), Path.Combine(link, "b.pak")));
    }

    [Fact]
    public void SameFile_SeesThroughAShortName()
    {
        if (!OperatingSystem.IsWindows()) return;
        var longFolder = Path.Combine(_dir, "Another Long Folder Name");
        Directory.CreateDirectory(longFolder);
        File.WriteAllText(Path.Combine(longFolder, "a.pak"), "x");

        Assert.True(FinalPaths.SameFile(Path.Combine(longFolder, "a.pak"), Path.Combine(TestLinks.ShortName(longFolder), "a.pak")));
    }

    [Fact]
    public void SameFile_SeesAHardLinkAsTheSameFile_ButNotACopy()
    {
        if (!OperatingSystem.IsWindows()) return;
        var original = Path.Combine(_dir, "a.pak");
        File.WriteAllText(original, "x");
        var hardLink = Path.Combine(_dir, "b.pak");
        TestLinks.MakeHardLink(hardLink, original);
        var copy = Path.Combine(_dir, "c.pak");
        File.Copy(original, copy);

        Assert.True(FinalPaths.SameFile(original, hardLink));
        Assert.False(FinalPaths.SameFile(original, copy));
    }

    [Fact]
    public void IsInside_ChecksTheResolvedPaths_AndNotALookalikeSibling()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(_dir, "mod");
        var outside = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(outside);
        _links.MakeJunction(Path.Combine(outside, "into"), folder);

        Assert.True(FinalPaths.IsInside(Path.Combine(outside, "into", "out.pak"), folder));
        Assert.False(FinalPaths.IsInside(Path.Combine(_dir, "mod2", "out.pak"), folder));
        Assert.False(FinalPaths.IsInside(folder, folder));
    }

    [Fact]
    public void ReachesInto_SeesAFileOutsideThatIsAHardLinkToOneInside_WhichIsInsideDoesNot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(_dir, "src");
        var outside = Path.Combine(_dir, "out");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(folder, "y.pak"), "x");
        TestLinks.MakeHardLink(Path.Combine(outside, "x.pak"), Path.Combine(folder, "y.pak"));
        File.WriteAllText(Path.Combine(outside, "z.pak"), "x");
        TestLinks.MakeHardLink(Path.Combine(outside, "w.pak"), Path.Combine(outside, "z.pak"));

        Assert.True(FinalPaths.ReachesInto(Path.Combine(outside, "x.pak"), folder));
        Assert.False(FinalPaths.ReachesInto(Path.Combine(outside, "w.pak"), folder));
        Assert.False(FinalPaths.ReachesInto(Path.Combine(outside, "new.pak"), folder));
        Assert.True(FinalPaths.ReachesInto(Path.Combine(folder, "new.pak"), folder));
        Assert.False(FinalPaths.IsInside(Path.Combine(outside, "x.pak"), folder));
    }

    [Fact]
    public void FileKey_IsSharedByHardLinks_ButNotByACopyOrAMissingFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var original = Path.Combine(_dir, "a.pak");
        File.WriteAllText(original, "x");
        var hardLink = Path.Combine(_dir, "b.pak");
        TestLinks.MakeHardLink(hardLink, original);
        var copy = Path.Combine(_dir, "c.pak");
        File.Copy(original, copy);

        Assert.NotNull(FinalPaths.FileKey(original));
        Assert.Equal(FinalPaths.FileKey(original), FinalPaths.FileKey(hardLink));
        Assert.NotEqual(FinalPaths.FileKey(original), FinalPaths.FileKey(copy));
        Assert.Null(FinalPaths.FileKey(Path.Combine(_dir, "missing.pak")));
    }

    [Fact]
    public void IsInside_TakesADriveRootAsTheFolder()
    {
        var root = Path.GetPathRoot(_dir)!;

        Assert.True(FinalPaths.IsInside(Path.Combine(_dir, "a.pak"), root));
    }

    [Fact]
    public void IsInside_FollowsALinkWhoseTargetIsMissing_InsteadOfTrustingItsName()
    {
        if (!OperatingSystem.IsWindows()) return;
        var destination = Path.Combine(_dir, "dest");
        Directory.CreateDirectory(destination);
        var link = Path.Combine(destination, "Content");
        _links.MakeJunction(link, Path.Combine(_dir, "outside", "missing"));

        // A write through the link lands outside the destination once its target appears.
        Assert.False(FinalPaths.IsInside(Path.Combine(link, "a.uasset"), destination));
    }

    [Theory]
    [InlineData(5)] // ERROR_ACCESS_DENIED
    [InlineData(32)] // ERROR_SHARING_VIOLATION
    [InlineData(53)] // ERROR_BAD_NETPATH: a link to a share whose host is offline
    [InlineData(67)] // ERROR_BAD_NET_NAME: a link to a share that is gone
    [InlineData(1921)] // ERROR_CANT_RESOLVE_FILENAME
    public void ResolveExisting_RefusesAPathTheFileSystemCouldNotPlace_NamingTheCause(int error)
    {
        var plain = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(plain);

        var refused = Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveExisting(plain, Failing(error)));

        Assert.Contains(plain, refused.Message, StringComparison.Ordinal);
        if (error != 1921) Assert.Contains(Marshal.GetPInvokeErrorMessage(error), refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)] // ERROR_INVALID_FUNCTION: a RAM disk
    [InlineData(50)] // ERROR_NOT_SUPPORTED: a VM shared folder or WebDAV
    public void ResolveExisting_KeepsThePathAsWrittenWithItsShortNamesWrittenOut_WhenTheVolumeReportsNoFinalPaths(int error)
    {
        // The long name stands in for what GetLongPathName returns for the short one.
        var shortSpelling = Path.Combine(_dir, "MODSOU~1", "out.pak");

        var resolved = FinalPaths.ResolveExisting(shortSpelling, Failing(error), path => path.Replace("MODSOU~1", "Mod Source", StringComparison.Ordinal));

        Assert.Equal(Path.Combine(_dir, "Mod Source", "out.pak"), resolved);
    }

    [Fact]
    public void ResolveExisting_RefusesAPathWhoseLongNameCannotBeRead_OnAVolumeThatReportsNoFinalPaths()
    {
        var refused = Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveExisting(_dir, Failing(1), _ => null));

        Assert.Contains(_dir, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveExisting_GivesAShortNameItsLongForm_WhenTheVolumeReportsNoFinalPaths_OnTheRealVolume()
    {
        if (!OperatingSystem.IsWindows()) return;
        var longFolder = Path.Combine(_dir, "A Folder With A Long Name");
        Directory.CreateDirectory(longFolder);

        Assert.Equal(FinalPaths.Resolve(longFolder), FinalPaths.ResolveExisting(TestLinks.ShortName(longFolder), Failing(1)));
    }

    [Fact]
    public void ResolveExisting_FollowsALinkByName_WhenTheVolumeReportsNoFinalPaths_InsteadOfTrustingItsSpelling()
    {
        // A junction on a RAM disk that leads elsewhere must not pass for a path inside its own folder.
        var real = Path.Combine(_dir, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_dir, "link");
        _links.MakeFolderLink(link, real);

        Assert.Equal(real, FinalPaths.ResolveExisting(link, Failing(1), longPathOf: path => path));
    }

    [Fact]
    public void ResolveExisting_RefusesAVolumeWithNoDriveLetter_NamingIt_InsteadOfCallingItMissing()
    {
        var refused = Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveExisting(_dir, Failing(FinalPaths.VolumeHasNoDriveLetter)));

        Assert.Contains("no drive letter", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\?\Volume{0f1e2d3c}\Mods")] // a volume GUID
    [InlineData(@"\\.\Volume{0f1e2d3c}\Mods")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\Mods")] // stripped to a relative form by the lookup
    [InlineData(@"C:\Allowed\nope\..\..\Windows")] // a dot segment survives
    [InlineData(@"C:\Allowed\.\Mods")]
    [InlineData(@"relative\Mods")]
    public void ResolveFull_LetsOnlyAPlainDriveOrNetworkPathLeave_WhateverBranchProducedIt(string produced)
    {
        // Two spellings of one folder would break every comparison of resolved paths.
        if (!OperatingSystem.IsWindows()) return;

        Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveFull(Path.Combine(_dir, "out.pak"), _ => new FinalPaths.HandleLookup(produced, 0), path => path, new FinalPaths.LinkBudget()));
    }

    [Fact]
    public void ResolveFull_RefusesAPathWhoseRootDoesNotExist_WhenItIsNotInPlainForm()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveFull(@"\\?\Z:\Game\a.uasset", null, path => path, new FinalPaths.LinkBudget()));
    }

    [Theory]
    [InlineData(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\x")] // a volume that is not mounted
    [InlineData(@"\\.\Volume{00000000-0000-0000-0000-000000000000}\x")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\C:")]
    [InlineData(@"\??\Volume{00000000-0000-0000-0000-000000000000}\x")]
    public void Resolve_RefusesADeviceOrVolumeSpelling(string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Throws<PathNotPlaceableException>(() => FinalPaths.Resolve(path));
    }

    [Theory]
    [InlineData("safe.")]
    [InlineData("safe ")]
    public void Resolve_RefusesAnExtendedNameThatEndsInADotOrSpace_ThatGetFullPathWouldTrimToAnotherFolder(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        var spelled = @"\\?\" + Path.Combine(_dir, name, "x.pak");

        Assert.Throws<PathNotPlaceableException>(() => FinalPaths.Resolve(spelled));
        Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveLinks(Path.Combine(_dir, "link"), path => path.EndsWith("link", StringComparison.Ordinal) ? spelled : null, new FinalPaths.LinkBudget()));
    }

    [Fact]
    public void Normalise_LeavesABackslashNameAlone_WhereBackslashIsAnOrdinaryCharacter()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Equal(Path.GetFullPath(@"\??\UNC\x"), FinalPaths.Normalise(@"\??\UNC\x"));
        Assert.Equal(Path.GetFullPath(@"\\?\C:\a"), FinalPaths.Normalise(@"\\?\C:\a"));
    }

    [Fact]
    public void Resolve_ReadsAnExtendedDriveSpellingAsThePlainOne_AndCollapsesItsDotDotSegments()
    {
        if (!OperatingSystem.IsWindows()) return;
        var plain = FinalPaths.Resolve(Path.Combine(_dir, "x.pak"));

        Assert.Equal(plain, FinalPaths.Resolve(@"\\?\" + Path.Combine(_dir, "x.pak")));
        Assert.Equal(plain, FinalPaths.Resolve(@"\\.\" + Path.Combine(_dir, "x.pak")));
        Assert.Equal(Path.Combine(FinalPaths.Resolve(Path.GetDirectoryName(_dir)!), "Windows", "x"), FinalPaths.Resolve(@"\\?\" + Path.Combine(_dir, "nope", "..", "..", "Windows", "x")));
    }

    [Fact]
    public void IsInside_DoesNotBeFooledByDotDotBehindAnExtendedPrefix()
    {
        if (!OperatingSystem.IsWindows()) return;
        var allowed = Path.Combine(_dir, "Allowed");
        Directory.CreateDirectory(allowed);

        Assert.False(FinalPaths.IsInside(@"\\?\" + Path.Combine(allowed, "nope", "..", "..", "Windows", "x"), allowed));
        Assert.True(FinalPaths.IsInside(@"\\?\" + Path.Combine(allowed, "sub", "x"), allowed));
    }

    [Fact]
    public void ResolveFull_GivesTheDosFormOfAnExtendedSpelling_OnAVolumeThatReportsNoFinalPaths()
    {
        if (!OperatingSystem.IsWindows()) return;
        var spelled = FinalPaths.Normalise(@"\\?\" + Path.Combine(_dir, "a.uasset"));

        Assert.Equal(Path.Combine(_dir, "a.uasset"), FinalPaths.ResolveFull(spelled, Failing(1), path => path, new FinalPaths.LinkBudget()));
    }

    [Fact]
    public void ResolveLinks_ReadsAPrefixedLinkTargetAsThePlainOne_AndRefusesADeviceTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var link = Path.Combine(_dir, "link");
        string? Target(string path, string target) => string.Equals(path, link, StringComparison.OrdinalIgnoreCase) ? target : null;

        Assert.Equal(@"R:\Game", FinalPaths.ResolveLinks(link, path => Target(path, @"\\?\R:\Game"), new FinalPaths.LinkBudget()));
        Assert.Equal(@"\\srv\share\Game", FinalPaths.ResolveLinks(link, path => Target(path, @"\\?\UNC\srv\share\Game"), new FinalPaths.LinkBudget()));
        Assert.Equal(@"R:\Windows", FinalPaths.ResolveLinks(link, path => Target(path, @"\\?\R:\Game\..\Windows"), new FinalPaths.LinkBudget()));
        Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveLinks(link, path => Target(path, @"\\?\Volume{0f1e2d3c}\Game"), new FinalPaths.LinkBudget()));
    }

    [Fact]
    public void ResolveExisting_PlacesTheTargetOfALinkOnAVolumeThatReportsNoFinalPaths_ThroughTheLookup()
    {
        // The target is on a normal volume, so it gets the handle check: here the lookup refuses it.
        var real = Path.Combine(_dir, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_dir, "link");
        _links.MakeFolderLink(link, real);
        FinalPaths.HandleLookup Lookup(string path) => string.Equals(path, link, StringComparison.OrdinalIgnoreCase) ? new(null, 1) : new(null, 5);

        var refused = Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveExisting(link, Lookup, longPathOf: path => path));

        Assert.Contains(real, refused.Message, StringComparison.Ordinal);
    }

    private static Func<string, FinalPaths.HandleLookup> Failing(int error) => _ => new FinalPaths.HandleLookup(null, error);

    // Fails only for one path, as the file system does for a link whose target is missing.
    private static Func<string, FinalPaths.HandleLookup> FailingAt(string failing, int error) =>
        path => string.Equals(path, failing, StringComparison.OrdinalIgnoreCase) ? new FinalPaths.HandleLookup(null, error) : new FinalPaths.HandleLookup(path, 0);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveLinks_RefusesALinkItCannotRead_RatherThanLettingTheRawErrorOut(bool unauthorized)
    {
        var path = Path.Combine(_dir, "unreadable", "a.pak");
        string? Unreadable(string next) => throw (unauthorized ? new UnauthorizedAccessException("denied") : new IOException("broken"));

        var refused = Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveLinks(path, Unreadable, new FinalPaths.LinkBudget()));

        Assert.Contains("unreadable", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_OfALinkWhoseMissingTargetIsSpelledInAnotherCase_GivesTheOnDiskCaseOfWhatExists()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(_dir, "outside"));
        var link = Path.Combine(_dir, "link");
        _links.MakeJunction(link, Path.Combine(_dir.ToUpperInvariant(), "OUTSIDE", "missing"));

        var resolved = FinalPaths.Resolve(Path.Combine(link, "a.uasset"));

        Assert.Equal(Path.Combine(FinalPaths.Resolve(_dir), "outside", "missing", "a.uasset"), resolved);
    }

    [Theory]
    [InlineData(2)] // ERROR_FILE_NOT_FOUND
    [InlineData(3)] // ERROR_PATH_NOT_FOUND
    public void ResolveExisting_FollowsTheLinksByName_WhenTheTargetIsMissing(int error)
    {
        var target = Path.Combine(_dir, "outside", "missing");
        var link = Path.Combine(_dir, "link");
        _links.MakeFolderLink(link, target);

        Assert.Equal(target, FinalPaths.ResolveExisting(link, FailingAt(link, error)));
    }

    [Fact]
    public void ResolveExisting_RefusesWhenThePartItFollowedToStillCannotBePlaced()
    {
        var link = Path.Combine(_dir, "link");
        _links.MakeFolderLink(link, Path.Combine(_dir, "outside", "missing"));
        var plain = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(plain);

        Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveExisting(link, Failing(2)));
        Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveExisting(plain, Failing(2)));
    }

    [Fact]
    public void ResolveExisting_SharesOneHopBudgetAcrossEveryLinkItFollows()
    {
        var second = Path.Combine(_dir, "second");
        _links.MakeFolderLink(second, Path.Combine(_dir, "outside", "missing"));
        var first = Path.Combine(_dir, "first");
        _links.MakeFolderLink(first, second);

        // Two links to follow and one re-entry to place the result: three hops, which fit at 37 spent and not at 38.
        Assert.Equal(Path.Combine(_dir, "outside", "missing"), FinalPaths.ResolveExisting(first, FailingAt(first, 2), budget: new FinalPaths.LinkBudget(spent: 37)));
        Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveExisting(first, FailingAt(first, 2), budget: new FinalPaths.LinkBudget(spent: 38)));
    }

    [Fact]
    public void ResolveExisting_GivesWhatTheFileSystemSaid_WhenItCouldPlaceThePath()
    {
        Assert.Equal(@"D:\Real\Place", FinalPaths.ResolveExisting(_dir, _ => new FinalPaths.HandleLookup(@"D:\Real\Place", 0)));
    }

    [Fact]
    public void Resolve_OfALinkCycle_RefusesWithAClearError_OnEveryPlatform()
    {
        var first = Path.Combine(_dir, "first");
        var second = Path.Combine(_dir, "second");
        _links.MakeFolderLink(first, second);
        _links.MakeFolderLink(second, first);

        var error = Assert.Throws<PathNotPlaceableException>(() => FinalPaths.Resolve(Path.Combine(first, "a.pak")));

        Assert.Contains("Too many levels of links", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileInformation_MatchesTheNativeLayout()
    {
        // BY_HANDLE_FILE_INFORMATION: one DWORD, three FILETIMEs of two DWORDs each, then six DWORDs, all 4-byte aligned.
        var layout = typeof(FinalPaths).GetNestedType("ByHandleFileInformation", System.Reflection.BindingFlags.NonPublic)!;

        Assert.Equal(52, System.Runtime.InteropServices.Marshal.SizeOf(layout));
    }
}
