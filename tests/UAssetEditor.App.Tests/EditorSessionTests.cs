using System.Text.Json;
using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.Search;

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
            // Shape-correct dummy (64 hex chars): this test only round-trips the field through
            // JSON, so a real game's key would add nothing but put one in the repository.
            AesKeyHex = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF",
            CreateBackup = false,
        };

        var json = JsonSerializer.Serialize(session, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<EditorSession>(json, JsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(EditorSession.CurrentSchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal(session.SourcePath, roundTripped.SourcePath);
        Assert.Equal(session.UsmapPath, roundTripped.UsmapPath);
        Assert.Equal(session.AesKeyHex, roundTripped.AesKeyHex);
        Assert.False(roundTripped.CreateBackup);
    }

    [Fact]
    public void Deserialize_JsonSavedBeforeAesKeyHexExisted_DefaultsToEmpty()
    {
        // Simulates a real pre-existing config file saved before this field was added - the
        // JSON simply has no "AesKeyHex" property, and must still load instead of throwing.
        const string legacyJson = """{ "SourcePath": "D:\\Old\\Source" }""";

        var session = JsonSerializer.Deserialize<EditorSession>(legacyJson, JsonOptions);

        Assert.NotNull(session);
        Assert.Equal("", session.AesKeyHex);
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
    public void Serialize_ThenDeserialize_RoundTripsTreeSelectNameTerms()
    {
        // Regression test: the Browse tree's "name contains" filter used to be excluded from
        // the saved session on purpose (a one-shot action term, not a lasting search scope) -
        // it's now persisted the same way the search-scope term boxes are, so a name filter
        // typed in a past session doesn't have to be retyped every launch.
        var session = new EditorSession
        {
            SchemaVersion = EditorSession.CurrentSchemaVersion,
            TreeSelectNameTerms = { new ConditionTerm("Post_", TermTag.And) },
        };

        var json = JsonSerializer.Serialize(session, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<EditorSession>(json, JsonOptions);

        Assert.NotNull(roundTripped);
        var term = Assert.Single(roundTripped.TreeSelectNameTerms);
        Assert.Equal("Post_", term.Text);
        Assert.Equal(TermTag.And, term.Tag);
    }

    [Fact]
    public void Deserialize_JsonSavedBeforeTreeSelectNameTermsExisted_DefaultsToEmpty()
    {
        const string legacyJson = """{ "SourcePath": "D:\\Old\\Source" }""";

        var session = JsonSerializer.Deserialize<EditorSession>(legacyJson, JsonOptions);

        Assert.NotNull(session);
        Assert.Empty(session.TreeSelectNameTerms);
    }

    [Fact]
    public void RecentSourceEntry_DisplayName_IsJustTheFileName()
    {
        var entry = new RecentSourceEntry(@"D:\Games\Example\Content.pak", UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_3, "", null);

        Assert.Equal("Content.pak", entry.DisplayName);
    }
}
