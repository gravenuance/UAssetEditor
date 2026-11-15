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
    public void ResolveExisting_UsesTheVolumeGuidForm_WhenAVolumeMountedAtAFolderHasNoDosForm()
    {
        // Path not found on a handle that did open: the volume has no drive letter.
        static FinalPaths.HandleLookup Lookup(string path, bool guidForm) =>
            guidForm ? new FinalPaths.HandleLookup(@"\\?\Volume{0f1e2d3c}\Mods\out", 0) : new FinalPaths.HandleLookup(null, 3, Opened: true);

        Assert.Equal(@"\\?\Volume{0f1e2d3c}\Mods\out", FinalPaths.ResolveExisting(_dir, Lookup));
    }

    private static Func<string, bool, FinalPaths.HandleLookup> Failing(int error) => (_, _) => new FinalPaths.HandleLookup(null, error);

    // Fails only for one path, as the file system does for a link whose target is missing.
    private static Func<string, bool, FinalPaths.HandleLookup> FailingAt(string failing, int error) =>
        (path, _) => string.Equals(path, failing, StringComparison.OrdinalIgnoreCase) ? new FinalPaths.HandleLookup(null, error) : new FinalPaths.HandleLookup(path, 0);

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

        // Two links to follow: the budget has room for them at 38 spent and not at 39.
        Assert.Equal(Path.Combine(_dir, "outside", "missing"), FinalPaths.ResolveExisting(first, FailingAt(first, 2), budget: new FinalPaths.LinkBudget(spent: 38)));
        Assert.Throws<PathNotPlaceableException>(() => FinalPaths.ResolveExisting(first, FailingAt(first, 2), budget: new FinalPaths.LinkBudget(spent: 39)));
    }

    [Fact]
    public void ResolveExisting_GivesWhatTheFileSystemSaid_WhenItCouldPlaceThePath()
    {
        Assert.Equal(@"D:\Real\Place", FinalPaths.ResolveExisting(_dir, (_, _) => new FinalPaths.HandleLookup(@"D:\Real\Place", 0)));
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
