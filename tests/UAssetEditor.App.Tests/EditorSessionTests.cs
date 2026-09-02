using System.Text.Json;
using UAssetEditor.App.ViewModels;

namespace UAssetEditor.App.Tests;

/// <summary>
/// Covers the schema-versioning behavior MainViewModel's SaveConfig/LoadConfig relies on -
/// MainViewModel itself isn't tested here (its ConfigPath is a hardcoded real LocalAppData
/// path with no way to inject a test location, so constructing it would read/depend on
/// whatever the running machine's actual saved session happens to be - not deterministic).
/// </summary>
public class EditorSessionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public void SchemaVersion_DefaultsToZero_NotCurrentVersion()
    {
        // A freshly-constructed session (the shape a config file saved before SchemaVersion
        // existed would deserialize into) must read as "unversioned", not silently appear
        // current - LoadConfig's forward-compatibility check depends on this distinction.
        var session = new EditorSession();

        Assert.Equal(0, session.SchemaVersion);
        Assert.NotEqual(EditorSession.CurrentSchemaVersion, session.SchemaVersion);
    }

    [Fact]
    public void Serialize_ThenDeserialize_RoundTripsTheCurrentSchemaVersion()
    {
        var session = new EditorSession
        {
            SchemaVersion = EditorSession.CurrentSchemaVersion,
            SourcePath = @"C:\Games\Example\Content.pak",
            UsmapPath = @"C:\Games\Example\Mappings.usmap",
            CreateBackup = false,
        };

        var json = JsonSerializer.Serialize(session, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<EditorSession>(json, JsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(EditorSession.CurrentSchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal(session.SourcePath, roundTripped.SourcePath);
        Assert.Equal(session.UsmapPath, roundTripped.UsmapPath);
        Assert.False(roundTripped.CreateBackup);
    }

    [Fact]
    public void Deserialize_JsonSavedBeforeSchemaVersionExisted_ReadsAsUnversioned()
    {
        // Simulates an actual pre-existing config file on disk from before this field was
        // added - the JSON simply has no "SchemaVersion" property at all.
        const string legacyJson = """{ "SourcePath": "D:\\Old\\Source" }""";

        var session = JsonSerializer.Deserialize<EditorSession>(legacyJson, JsonOptions);

        Assert.NotNull(session);
        Assert.Equal(0, session.SchemaVersion);
        Assert.Equal(@"D:\Old\Source", session.SourcePath);
    }

    [Fact]
    public void RecentSourceEntry_DisplayName_IsJustTheFileName()
    {
        var entry = new RecentSourceEntry(@"D:\Games\Example\Content.pak", UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_3, "", null);

        Assert.Equal("Content.pak", entry.DisplayName);
    }
}
