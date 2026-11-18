using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.Tests;

public class PakWorkerOodleTests
{
    [Fact]
    public void ResolveOodleLibrary_FindsTheVendoredDll_SoTheWorkerCanBeToldWhereItIs()
    {
        var path = PakWorkerProcess.ResolveOodleLibrary();

        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"{path} does not exist");
        Assert.EndsWith("oo2core_9_win64.dll", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OodleEnvVar_IsTheNameRepakBindReads()
    {
        Assert.Equal("REPAK_OODLE_DLL", PakWorkerProcess.OodleEnvVar);
    }
}
