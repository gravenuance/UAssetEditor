using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using UAssetEditor.Core;
using UAssetEditor.Core.AssetSources.PakWorker;
using UAssetEditor.Core.Games;
using UAssetEditor.Mcp.Hosting;
using UAssetEditor.Mcp.Tools;

const string CommandName = "UAssetEditor.Mcp";

switch (args)
{
    case []:
        break;
    case ["--version"]:
        await Console.Out.WriteLineAsync(AppVersion()).ConfigureAwait(false);
        return 0;
    case ["-h" or "--help"]:
        await Console.Out.WriteLineAsync(HelpText()).ConfigureAwait(false);
        return 0;
    default:
        await Console.Error.WriteLineAsync($"Unknown argument: {args[0]}{Environment.NewLine}Run {CommandName} --help for usage.").ConfigureAwait(false);
        return 2;
}

// Edits and searches take regular expressions from the model; a pattern that backtracks forever must not hang a tool call.
AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromSeconds(3));

var paths = AppPaths.Current;
var level = McpLogging.ParseLevel(Environment.GetEnvironmentVariable(McpLogging.LevelVariable));
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
try
{
    McpLogging.Configure(builder.Logging, paths, level);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync($"{CommandName} could not create its log folder {McpLogging.LogDirectory(paths)}: {ex.Message}").ConfigureAwait(false);
    return 1;
}

var store = MappingsStore.Default();
builder.Services.AddUAssetMcpServices(store);
builder.Services.AddMcpServer(options => options.ServerInstructions = ServerInstructions.Text)
    .WithStdioServerTransport()
    .WithRequestFilters(filters => filters.AddCallToolFilter(ToolErrorFilter.Wrap))
    .WithTools(toolTypes: ServiceRegistration.ToolTypes); // by name: a bare list would bind to the overload that takes a tool instance

// Backstop for a kill that skips the finally below: the shared pak worker must not outlive the server.
AppDomain.CurrentDomain.ProcessExit += (_, _) => PakWorkerProcess.Shared.Dispose();

using var host = builder.Build();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(CommandName);
McpLogging.HandleProcessWideFailures(log);
try
{
    log.LogInformation(
        "{Command} {Version} starting on {OS}; cache {CacheDirectory}, usmap store {UsmapStore}, log level {Level}",
        CommandName,
        AppVersion(),
        System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        paths.CacheRoot,
        store.Root,
        level);
    McpLogging.PruneOldLogs(McpLogging.LogDirectory(paths), DateTimeOffset.UtcNow);

    await host.RunAsync().ConfigureAwait(false);
    log.LogInformation("{Command} stopped", CommandName);
    return 0;
}
catch (Exception ex)
{
    log.LogCritical(ex, "{Command} stopped unexpectedly", CommandName);
    await Console.Error.WriteLineAsync($"{CommandName} stopped unexpectedly; see the log in {McpLogging.LogDirectory(paths)}").ConfigureAwait(false);
    return 1;
}
finally
{
    // Every pak tool spawns the shared out-of-process pak worker on first use; this process would leave it running.
    PakWorkerProcess.Shared.Dispose();
}

static string AppVersion() =>
    typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

static string HelpText() => $"""
    UAssetEditor.Mcp - local MCP server that lets an agent read and edit Unreal Engine assets and archives through UAssetEditor.Core.

    Usage:
      UAssetEditor.Mcp             Run the MCP server on stdio (the MCP client starts it).
      UAssetEditor.Mcp --version   Print the version.
      UAssetEditor.Mcp -h, --help  Show this help.

    Read tools: list_exports, list_imports, get_tree, dump_export, search_assets, preview_edit, preview_ruleset,
    pak_info, pak_list, iostore_list, usmap_list, game_log_summary.
    Tools that write: edit_asset, apply_ruleset, unpack_pak, pack, to_legacy, repack, usmap_add.
    The pak and IoStore tools need Windows; the asset tools run anywhere.

    Environment: UASSETEDITOR_CACHE_DIR (logs and the extracted helper tools), UASSETEDITOR_MAPPINGS_DIR (the usmap store),
    {McpLogging.LevelVariable} (Information, Warning or Error). Logs go to <cache>/Logs/Mcp.

    Exit codes: 0 success, 1 failure, 2 usage error.
    """;
