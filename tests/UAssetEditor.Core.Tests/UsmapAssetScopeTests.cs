using System.Collections.Concurrent;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.Tests;

public class UsmapAssetScopeTests
{
    [Fact]
    public void AssetScope_ReadsTheSharedSchemasButKeepsItsOwnWritesPrivate()
    {
        var shared = CreateMappings("Shared");
        var first = shared.CreateAssetScope();
        var second = shared.CreateAssetScope();

        first.Schemas["Post_A_C"] = Schema("Post_A_C");
        first.Schemas["AnimBlueprintGeneratedConstantData"] = Schema("FromFirst");
        second.Schemas["AnimBlueprintGeneratedConstantData"] = Schema("FromSecond");

        Assert.Same(shared.Schemas["Shared"], first.Schemas["shared"]);
        Assert.True(first.Schemas.ContainsKey("Post_A_C"));
        Assert.False(second.Schemas.ContainsKey("Post_A_C"));
        Assert.False(shared.Schemas.ContainsKey("Post_A_C"));
        Assert.Equal("FromFirst", first.Schemas["AnimBlueprintGeneratedConstantData"].Name);
        Assert.Equal("FromSecond", second.Schemas["AnimBlueprintGeneratedConstantData"].Name);
        Assert.Equal(3, first.Schemas.Count);
    }

    [Fact]
    public void AssetScope_ALocalWriteShadowsTheSharedEntry()
    {
        var shared = CreateMappings("Shared");
        var scope = shared.CreateAssetScope();

        scope.Schemas["Shared"] = Schema("Local");

        Assert.Equal("Local", scope.Schemas["Shared"].Name);
        Assert.Equal("Shared", shared.Schemas["Shared"].Name);
        Assert.Single(scope.Schemas);
    }

    private static Usmap CreateMappings(string schemaName) => new()
    {
        Schemas = new ConcurrentDictionary<string, UsmapSchema>(StringComparer.OrdinalIgnoreCase) { [schemaName] = Schema(schemaName) },
        EnumMap = new ConcurrentDictionary<string, UsmapEnum>(StringComparer.OrdinalIgnoreCase),
    };

    private static UsmapSchema Schema(string name) => new(name, null, 0, new ConcurrentDictionary<int, UsmapProperty>(), true, null);
}
