namespace UAssetEditor.Core.Tests;

public class AppPathsTests
{
    private static readonly string Fallback = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UAssetEditorTest_AppPaths_Fallback"));
    private static readonly string Override = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UAssetEditorTest_AppPaths_Override"));

    private static AppPaths Create(string? variableValue) =>
        new(name => name == AppPaths.CacheDirectoryVariable ? variableValue : null, Fallback);

    [Fact]
    public void VariableName_IsUassetEditorCacheDir()
    {
        Assert.Equal("UASSETEDITOR_CACHE_DIR", AppPaths.CacheDirectoryVariable);
    }

    [Fact]
    public void CacheRoot_WithVariableSet_IsTheVariablesFolder()
    {
        Assert.Equal(Override, Create(Override).CacheRoot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CacheRoot_WithVariableUnsetOrBlank_IsTheDefault(string? value)
    {
        Assert.Equal(Fallback, Create(value).CacheRoot);
    }

    [Fact]
    public void CacheRoot_WithRelativeVariable_IsMadeAbsolute()
    {
        Assert.Equal(Path.GetFullPath("relative-cache"), Create("relative-cache").CacheRoot);
    }

    [Fact]
    public void LogsAndToolsDirectories_SitUnderTheCacheRoot()
    {
        var paths = Create(Override);

        Assert.Equal(Path.Combine(Override, "Logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(Override, "Tools"), paths.ToolsDirectory);
    }
}
