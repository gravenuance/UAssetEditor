using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.Tests;

/// <summary>Every layout is built from empty placeholder files in a temp folder: no real Oodle DLL is needed anywhere.</summary>
public sealed class OodleLibraryTests : IDisposable
{
    private const string RepakError = "pointer to OodleLZ_Decompress was not provided";

    private static readonly Func<string, string?> NoEnvironment = _ => null;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Oodle_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Touch(params string[] relativeParts)
    {
        var path = Path.Combine([_root, .. relativeParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    /// <summary>A pak file at <c>&lt;root&gt;/&lt;relative folder&gt;/game.pak</c>; the folder is created, the pak itself is not read.</summary>
    private string PakIn(params string[] relativeFolder) => Touch([.. relativeFolder, "game.pak"]);

    private static Func<string, string?> EnvWith(string? value) =>
        name => name == OodleLibrary.EnvironmentVariable ? value : null;

    [Fact]
    public void VariableName_IsTheOneRepakBindReads()
    {
        Assert.Equal("REPAK_OODLE_DLL", OodleLibrary.EnvironmentVariable);
    }

    [Fact]
    public void Locate_WithAUsableEnvironmentVariable_UsesItEvenWhenTheGameHasADll()
    {
        var configured = Touch("elsewhere", "oo2core_8_win64.dll");
        Touch("Days Gone", "oo2core_9_win64.dll");
        var pak = PakIn("Days Gone", "BendGame", "Content", "Paks");

        var lookup = OodleLibrary.Locate(pak, EnvWith(configured));

        Assert.Equal(configured, lookup.Path);
        Assert.Null(lookup.RejectedEnvironmentValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Locate_WithABlankEnvironmentVariable_FallsBackToTheGameInstall(string blank)
    {
        var dll = Touch("Days Gone", "oo2core_9_win64.dll");
        var pak = PakIn("Days Gone", "BendGame", "Content", "Paks");

        Assert.Equal(dll, OodleLibrary.Locate(pak, EnvWith(blank)).Path);
    }

    [Fact]
    public void Locate_WithAMisnamedEnvironmentFile_RejectsItAndDoesNotSearchTheGame()
    {
        var misnamed = Touch("elsewhere", "payload.dll");
        Touch("Days Gone", "oo2core_9_win64.dll");
        var pak = PakIn("Days Gone", "BendGame", "Content", "Paks");

        var lookup = OodleLibrary.Locate(pak, EnvWith(misnamed));

        Assert.Null(lookup.Path);
        Assert.Equal(misnamed, lookup.RejectedEnvironmentValue);
    }

    [Fact]
    public void Locate_WithAMissingEnvironmentFile_RejectsIt()
    {
        var missing = Path.Combine(_root, "gone", "oo2core_9_win64.dll");

        var lookup = OodleLibrary.Locate(null, EnvWith(missing));

        Assert.Null(lookup.Path);
        Assert.Equal(missing, lookup.RejectedEnvironmentValue);
    }

    [Fact]
    public void Locate_WithAFolderNamedLikeTheDll_RejectsIt()
    {
        var folder = Path.Combine(_root, "oo2core_9_win64.dll");
        Directory.CreateDirectory(folder);

        Assert.Equal(folder, OodleLibrary.Locate(null, EnvWith(folder)).RejectedEnvironmentValue);
    }

    [Fact]
    public void Locate_WithARelativeEnvironmentPath_RejectsIt()
    {
        var lookup = OodleLibrary.Locate(null, EnvWith("oo2core_9_win64.dll"));

        Assert.Null(lookup.Path);
        Assert.Equal("oo2core_9_win64.dll", lookup.RejectedEnvironmentValue);
    }

    [Fact]
    public void Locate_WithNoPakAndNoVariable_FindsNothing()
    {
        Touch("oo2core_9_win64.dll");

        Assert.Equal(OodleLibrary.NotFound, OodleLibrary.Locate(null, NoEnvironment));
    }

    [Fact]
    public void Locate_FindsADllInTheGameRoot()
    {
        var dll = Touch("Game", "oo2core_9_win64.dll");
        var pak = PakIn("Game", "Project", "Content", "Paks");

        Assert.Equal(dll, OodleLibrary.Locate(pak, NoEnvironment).Path);
    }

    [Fact]
    public void Locate_FindsADllUnderTheEnginesThirdPartyOodleFolder()
    {
        var dll = Touch("Game", "Engine", "Binaries", "ThirdParty", "Oodle", "Win64", "oo2core_7_win64.dll");
        var pak = PakIn("Game", "End", "Content", "Paks");

        Assert.Equal(dll, OodleLibrary.Locate(pak, NoEnvironment).Path);
    }

    [Fact]
    public void Locate_FindsADllInAProjectsWin64BinariesFolder()
    {
        var dll = Touch("Game", "Marvel", "Binaries", "Win64", "oo2core_9_win64.dll");
        var pak = PakIn("Game", "Marvel", "Content", "Paks");

        Assert.Equal(dll, OodleLibrary.Locate(pak, NoEnvironment).Path);
    }

    [Fact]
    public void Locate_FromAPakInAModsSubfolder_StillReachesTheGameRoot()
    {
        var dll = Touch("Game", "oo2core_9_win64.dll");
        var pak = PakIn("Game", "Project", "Content", "Paks", "~mods", "Hero");

        Assert.Equal(dll, OodleLibrary.Locate(pak, NoEnvironment).Path);
    }

    [Fact]
    public void Locate_PicksTheHighestVersionAcrossTheSpots()
    {
        Touch("Game", "Engine", "Binaries", "ThirdParty", "Oodle", "Win64", "oo2core_7_win64.dll");
        Touch("Game", "oo2core_8_win64.dll");
        var newest = Touch("Game", "Project", "Binaries", "Win64", "oo2core_9_win64.dll");
        var pak = PakIn("Game", "Project", "Content", "Paks");

        Assert.Equal(newest, OodleLibrary.Locate(pak, NoEnvironment).Path);
    }

    [Fact]
    public void Locate_ComparesVersionsAsNumbersNotText()
    {
        Touch("Game", "oo2core_9_win64.dll");
        var ten = Touch("Game", "Project", "Binaries", "Win64", "oo2core_10_win64.dll");
        var pak = PakIn("Game", "Project", "Content", "Paks");

        Assert.Equal(ten, OodleLibrary.Locate(pak, NoEnvironment).Path);
    }

    [Fact]
    public void Locate_PrefersTheNearestInstallOverAHigherVersionFurtherUp()
    {
        Touch("Library", "oo2core_9_win64.dll");
        var inner = Touch("Library", "Game", "oo2core_7_win64.dll");
        var pak = PakIn("Library", "Game", "Project", "Content", "Paks");

        Assert.Equal(inner, OodleLibrary.Locate(pak, NoEnvironment).Path);
    }

    [Fact]
    public void Locate_StopsAfterTheParentLimit()
    {
        var dll = Touch("Game", "oo2core_9_win64.dll");
        var atLimit = PakIn("Game", "a", "b", "c", "d", "e");
        var pastLimit = PakIn("Game", "a", "b", "c", "d", "e", "f");

        Assert.Equal(5, OodleLibrary.MaxParentLevels);
        Assert.Equal(dll, OodleLibrary.Locate(atLimit, NoEnvironment).Path);
        Assert.Null(OodleLibrary.Locate(pastLimit, NoEnvironment).Path);
    }

    [Fact]
    public void Locate_StopsLookingDeepUnderTheOodleFolder()
    {
        var shallow = Touch("Game", "Engine", "Binaries", "ThirdParty", "Oodle", "Win64", "x86", "oo2core_8_win64.dll");
        Touch("Game", "Engine", "Binaries", "ThirdParty", "Oodle", "a", "b", "c", "d", "oo2core_9_win64.dll");
        var pak = PakIn("Game", "Project", "Content", "Paks");

        Assert.Equal(shallow, OodleLibrary.Locate(pak, NoEnvironment).Path);
    }

    [Fact]
    public void Locate_IgnoresFilesThatAreNotExactlyTheDllName()
    {
        Touch("Game", "oo2core_x_win64.dll");
        Touch("Game", "oo2core_9_win64.dll.bak");
        Touch("Game", "oo2core_9_win32.dll");
        Touch("Game", "xoo2core_9_win64.dll");
        Directory.CreateDirectory(Path.Combine(_root, "Game", "oo2core_8_win64.dll"));
        var pak = PakIn("Game", "Project", "Content", "Paks");

        Assert.Null(OodleLibrary.Locate(pak, NoEnvironment).Path);
    }

    [Fact]
    public void Locate_IgnoresADllInAnUnrelatedFolderBesideTheInstall()
    {
        Touch("Game", "Other", "Plugins", "oo2core_9_win64.dll");
        var pak = PakIn("Game", "Project", "Content", "Paks");

        Assert.Null(OodleLibrary.Locate(pak, NoEnvironment).Path);
    }

    [Fact]
    public void Explain_ForAnOodlePakWithNoDll_SaysWhatIsMissingAndHowToFixIt()
    {
        var message = OodleLibrary.Explain(RepakError, OodleLibrary.NotFound);

        Assert.Equal("Oodle DLL not found. Point REPAK_OODLE_DLL at the game's oo2core_*_win64.dll.", message);
    }

    [Fact]
    public void Explain_WhenTheVariableIsRejected_SaysToFixOrUnsetIt()
    {
        var message = OodleLibrary.Explain(RepakError, new OodleLookup(null, "D:/nope.dll"));

        Assert.Equal("REPAK_OODLE_DLL is not an existing oo2core_*_win64.dll file. Fix or unset it.", message);
    }

    [Fact]
    public void Explain_WhenAFoundDllWillNotLoad_NamesIt()
    {
        var message = OodleLibrary.Explain("read failed: " + RepakError, new OodleLookup("D:/Game/oo2core_9_win64.dll"));

        Assert.Equal("Could not load D:/Game/oo2core_9_win64.dll. Use a 64-bit oo2core_*_win64.dll.", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No entry found at Game/x.uasset")]
    public void Explain_ForAnyOtherError_ReturnsNull(string? error)
    {
        Assert.Null(OodleLibrary.Explain(error, OodleLibrary.NotFound));
    }
}
