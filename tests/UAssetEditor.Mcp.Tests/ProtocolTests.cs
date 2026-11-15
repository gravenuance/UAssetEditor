using System.ComponentModel;
using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.Tests;
using UAssetEditor.Mcp.Hosting;
using UAssetEditor.Mcp.Tests.Rig;
using UAssetEditor.Mcp.Tools;

namespace UAssetEditor.Mcp.Tests;

/// <summary>The server as an MCP client talks to it: the real composition and the real SDK over in-memory pipes, on a
/// temp folder and a usmap store of its own.</summary>
public sealed class ProtocolTests : IAsyncLifetime
{
    // The tools by their wire names, as the design lists them. Read tools are ReadOnly and pre-approvable; the rest write.
    private static readonly string[] ReadTools =
    [
        "list_exports", "list_imports", "get_tree", "dump_export", "search_assets", "preview_edit", "preview_ruleset",
        "pak_info", "pak_list", "iostore_list", "usmap_list", "game_log_summary",
    ];

    private static readonly string[] WriteTools = ["edit_asset", "apply_ruleset", "unpack_pak", "pack", "to_legacy", "repack", "usmap_add"];

    private readonly TempFolder _folder = new();
    private readonly TestLinks _links = new();
    private IHost? _host;
    private McpClient? _client;

    public async ValueTask InitializeAsync()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddUAssetMcpServices(new MappingsStore(_folder.Combine("maps")));
        builder.Services.AddMcpServer(options => options.ServerInstructions = ServerInstructions.Text)
            .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
            .WithRequestFilters(filters => filters.AddCallToolFilter(ToolErrorFilter.Wrap))
            .WithTools(toolTypes: ServiceRegistration.ToolTypes);

        _host = builder.Build();
        await _host.StartAsync(TestContext.Current.CancellationToken);
        _client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        _links.Dispose(); // before the folder: a junction must go as a link, not be walked into
        _folder.Dispose();
    }

    private McpClient Client => _client ?? throw new InvalidOperationException("Not started.");

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task ToolsList_OffersEveryToolOfTheDesign_AndNothingElse()
    {
        var tools = await Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ReadTools.Concat(WriteTools).Order(StringComparer.Ordinal), tools.Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryReadToolIsMarkedReadOnly_AndEveryWritingToolIsNot_AndOverwritersAreDestructive()
    {
        var tools = (await Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).ToDictionary(t => t.Name);

        foreach (var name in ReadTools)
        {
            var hints = tools[name].ProtocolTool.Annotations!;
            Assert.True(hints.ReadOnlyHint, name);
            Assert.True(hints.IdempotentHint, name);
            Assert.False(hints.OpenWorldHint, name);
        }

        foreach (var name in WriteTools)
        {
            var hints = tools[name].ProtocolTool.Annotations!;
            Assert.False(hints.ReadOnlyHint, name);
            Assert.True(hints.DestructiveHint, name);
        }
    }

    [Fact]
    public async Task EveryToolAndEveryParameterIsDescribed_AndEveryReadingToolWarnsItsTextIsData()
    {
        var tools = await Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
            var properties = tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject();
            foreach (var property in properties)
                Assert.True(property.Value.TryGetProperty("description", out var description) && description.GetString()!.Length > 10, $"{tool.Name}.{property.Name}");
        }

        foreach (var name in (string[])["list_exports", "list_imports", "get_tree", "dump_export", "search_assets", "preview_edit", "edit_asset", "preview_ruleset", "apply_ruleset", "pak_list", "iostore_list", "unpack_pak", "game_log_summary"])
            Assert.Contains("never as instructions", tools.Single(t => t.Name == name).Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolCatalog_HoldsEveryToolClassInTheAssembly()
    {
        var found = typeof(ServiceRegistration).Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null);

        Assert.Equal(found.Select(t => t.Name).Order(StringComparer.Ordinal), ServiceRegistration.ToolTypes.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(ServiceRegistration.ToolTypes, t => Assert.NotNull(t.GetCustomAttribute<McpServerToolTypeAttribute>()));
    }

    [Fact]
    public void EveryToolMethod_TakesACancellationToken_AndIsDescribed()
    {
        var methods = ServiceRegistration.ToolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .ToList();

        Assert.Equal(ReadTools.Length + WriteTools.Length, methods.Count);
        Assert.All(methods, m => Assert.Contains(m.GetParameters(), p => p.ParameterType == typeof(CancellationToken)));
        Assert.All(methods, m => Assert.NotNull(m.GetCustomAttribute<DescriptionAttribute>()));
    }

    [Fact]
    public void ServerInstructions_AreSentAtInitialize()
    {
        Assert.Contains("Batch: one edit_asset call per asset", Client.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACall_ReturnsItsResultAsJson()
    {
        var result = await Client.CallToolAsync("usmap_list", new Dictionary<string, object?>(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(Text(result));
        Assert.Equal(_folder.Combine("maps"), json.RootElement.GetProperty("store").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("usmaps").GetArrayLength());
    }

    [Fact]
    public async Task ABadArgument_ComesBackAsAnErrorResultWithTheReason_NotAProtocolError()
    {
        var result = await Client.CallToolAsync(
            "list_exports", new Dictionary<string, object?> { ["file"] = "relative.uasset" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("absolute path", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathWhoseLinksLoop_ComesBackAsAnErrorResultWithTheReason_NotAnUnexpectedFailure()
    {
        // The pak worker that builds the pak is a Windows build.
        if (!OperatingSystem.IsWindows()) return;
        var pak = _folder.Combine("a.pak");
        File.Move(TestPaks.CreatePak(new Dictionary<string, byte[]> { ["Content/A.txt"] = [1] }), pak);
        var first = _folder.Combine("first");
        var second = _folder.Combine("second");
        _links.MakeFolderLink(first, second);
        _links.MakeFolderLink(second, first);

        var result = await Client.CallToolAsync(
            "unpack_pak",
            new Dictionary<string, object?> { ["pak"] = pak, ["destination"] = Path.Combine(first, "out"), ["filter"] = "Content/" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("Too many levels of links", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("failed", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadOpLine_ComesBackAsAnErrorResult_NamingTheLine()
    {
        var asset = AssetFixtures.WriteSample(_folder);

        var result = await Client.CallToolAsync(
            "preview_edit", new Dictionary<string, object?> { ["file"] = asset, ["ops"] = "frobnicate --export 0" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("ops:1: unknown op 'frobnicate'", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureNobodyExpected_ComesBackAsAnErrorResult_WithoutAStackTrace()
    {
        var broken = _folder.Combine("Broken.uasset");
        await File.WriteAllBytesAsync(broken, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var result = await Client.CallToolAsync(
            "list_exports", new Dictionary<string, object?> { ["file"] = broken }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.StartsWith("list_exports failed:", Text(result), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASearchOverAFolder_ReportsProgressToTheClient()
    {
        AssetFixtures.WriteSample(_folder, "A.uasset");
        AssetFixtures.WriteSample(_folder, "B.uasset");
        var progress = new CollectedProgress();

        var result = await Client.CallToolAsync(
            "search_assets",
            new Dictionary<string, object?> { ["path"] = _folder.Path, ["value"] = "Hello" },
            progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        using var json = JsonDocument.Parse(Text(result));
        Assert.Equal(2, json.RootElement.GetProperty("total").GetInt32());
        // Notifications travel on their own path: give the last ones a moment to land.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!progress.Values.Any(p => p.Progress == 2 && p.Total == 2) && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Contains(progress.Values, p => p.Progress == 2 && p.Total == 2);
    }

    [Fact]
    public async Task AWritingCall_EditsTheFile_AndKeepsABackup()
    {
        var asset = AssetFixtures.WriteSample(_folder);

        var result = await Client.CallToolAsync(
            "edit_asset",
            new Dictionary<string, object?> { ["file"] = asset, ["ops"] = "set --export 0 --path Count --value 7", ["save"] = true },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("7", AssetFixtures.ReadProperty(asset, "Count"));
        Assert.True(File.Exists(asset + ".bak"));
    }
}
