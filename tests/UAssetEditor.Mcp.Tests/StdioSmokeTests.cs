using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using UAssetEditor.Mcp.Hosting;
using UAssetEditor.Mcp.Tests.Rig;

namespace UAssetEditor.Mcp.Tests;

/// <summary>The real program over real standard input and output: a child process started the way an MCP client starts it,
/// with its cache and usmap store pointed at a temp folder.</summary>
public sealed class StdioSmokeTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _folder.Dispose();

    private static string ServerDll => typeof(ServiceRegistration).Assembly.Location;

    private StdioClientTransport Transport() => new(new StdioClientTransportOptions
    {
        Name = "UAssetEditor.Mcp",
        Command = "dotnet",
        Arguments = [ServerDll],
        EnvironmentVariables = new Dictionary<string, string?>
        {
            ["UASSETEDITOR_CACHE_DIR"] = _folder.Combine("cache"),
            ["UASSETEDITOR_MAPPINGS_DIR"] = _folder.Combine("maps"),
        },
    });

    [Fact]
    public async Task TheServerStartsOverStdio_ListsItsTools_AnswersACall_AndLogsToAFileNotToStdout()
    {
        await using var client = await McpClient.CreateAsync(Transport(), cancellationToken: _ct);

        var tools = await client.ListToolsAsync(cancellationToken: _ct);
        var result = await client.CallToolAsync("usmap_list", new Dictionary<string, object?>(), cancellationToken: _ct);

        Assert.Equal(19, tools.Count);
        Assert.Equal("UAssetEditor.Mcp", client.ServerInfo?.Name);
        using var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(_folder.Combine("maps"), json.RootElement.GetProperty("store").GetString());

        await client.DisposeAsync();
        var logs = Directory.GetFiles(_folder.Combine("cache", "Logs", "Mcp"), "log-*.txt");
        var log = await File.ReadAllTextAsync(Assert.Single(logs), _ct);
        Assert.Contains("UAssetEditor.Mcp 0.1.0", log, StringComparison.Ordinal);
        Assert.Contains("starting on", log, StringComparison.Ordinal);
    }
}
