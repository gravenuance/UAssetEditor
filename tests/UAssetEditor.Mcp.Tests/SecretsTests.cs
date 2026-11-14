using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UAssetEditor.Core.AssetSources.IoStore;
using UAssetEditor.Mcp.Tools;

namespace UAssetEditor.Mcp.Tests;

public sealed class SecretsTests
{
    private const string Key = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF"; // gitleaks:allow (a made-up test key)

    [Theory]
    [InlineData("retoc to-legacy --aes-key 0x00112233 in out failed", "retoc to-legacy --aes-key [redacted] in out failed")]
    [InlineData("retoc --AES-KEY   abc in", "retoc --aes-key [redacted] in")]
    [InlineData("the key is 00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF here", "the key is [redacted] here")]
    [InlineData("the key is 0x00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff.", "the key is [redacted].")]
    public void Redact_ReplacesAKeyArgumentAndABareKey(string text, string expected)
    {
        Assert.Equal(expected, Secrets.Redact(text));
    }

    [Theory]
    [InlineData("nothing secret here")]
    [InlineData("a 63-digit run 00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEF stays")]
    [InlineData("a 65-digit run 00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF0 stays")]
    public void Redact_LeavesOtherTextAlone(string text)
    {
        Assert.Equal(text, Secrets.Redact(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_GivenNothing_ReturnsAnEmptyString(string? text)
    {
        Assert.Equal("", Secrets.Redact(text));
    }

    [McpServerToolType]
    internal static class FailingTools
    {
        [McpServerTool(Name = "retoc_failure")]
        [Description("Fails the way a retoc run does.")]
        public static string RetocFailure() =>
            throw new IoStoreConversionException(
                $"retoc.exe to-legacy --aes-key 0x{Key} a.utoc out failed: exit 1",
                "error: could not decrypt (key 0x" + Key + ")",
                1);

        [McpServerTool(Name = "wrapped_failure")]
        [Description("Fails with the key only in an inner exception.")]
        public static string WrappedFailure() =>
            throw new InvalidOperationException("outer", new InvalidOperationException($"inner {Key}"));

        [McpServerTool(Name = "bad_input")]
        [Description("Fails with a message that names the key.")]
        public static string BadInput() => throw new McpException($"key {Key} is not accepted");
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new Capture(_lines);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception) + (exception is null ? "" : Environment.NewLine + exception));
        }
    }

    private static async Task<(CallToolResult Result, IReadOnlyCollection<string> Logs)> CallAsync(string tool, CancellationToken ct)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        using var logs = new CapturingLogs();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
            .WithRequestFilters(filters => filters.AddCallToolFilter(ToolErrorFilter.Wrap))
            .WithTools(toolTypes: [typeof(FailingTools)]);

        using var host = builder.Build();
        await host.StartAsync(ct);
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: ct);
        var result = await client.CallToolAsync(tool, new Dictionary<string, object?>(), cancellationToken: ct);
        await client.DisposeAsync();
        await host.StopAsync(ct);
        return (result, logs.Lines);
    }

    [Theory]
    [InlineData("retoc_failure")]
    [InlineData("wrapped_failure")]
    [InlineData("bad_input")]
    public async Task AFailureThatMentionsTheKey_NeverReachesTheCallerOrTheLog_WithIt(string tool)
    {
        var (result, logs) = await CallAsync(tool, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var shown = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.DoesNotContain(Key, shown, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(logs);
        Assert.All(logs, line => Assert.DoesNotContain(Key, line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARetocFailure_ShowsTheCaller_ItsShortSummaryRatherThanTheCommandLine()
    {
        var (result, _) = await CallAsync("retoc_failure", TestContext.Current.CancellationToken);

        var shown = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.StartsWith("retoc_failure failed: IoStoreConversionException: error: could not decrypt", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("to-legacy", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnexpectedFailure_IsLoggedWithItsWholeChain_KeyRedacted()
    {
        var (_, logs) = await CallAsync("wrapped_failure", TestContext.Current.CancellationToken);

        var line = Assert.Single(logs, l => l.StartsWith("Tool wrapped_failure failed", StringComparison.Ordinal));
        Assert.Contains("InvalidOperationException: outer <- System.InvalidOperationException: inner [redacted]", line, StringComparison.Ordinal);
    }
}
