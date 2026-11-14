using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Turns tool failures into error results the caller can read: bad input and expected failures by their own
/// message, anything else by its type and message with the stack in the log. No exception reaches the transport.
/// Cancellation passes through, since the SDK answers it itself.</summary>
internal static class ToolErrorFilter
{
    private const int MaxMessageLength = 600;

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not McpProtocolException)
            {
                var log = request.Services?.GetService<ILoggerFactory>()?.CreateLogger("UAssetEditor.Mcp.Tools");
                var tool = request.Params?.Name ?? "?";
                if (ex is McpException or ArgException)
                {
                    log?.LogInformation("Tool {Tool} returned an error: {Reason}", tool, ex.Message);
                    return Error(ex.Message);
                }

                log?.LogError(ex, "Tool {Tool} failed", tool);
                return Error($"{tool} failed: {ex.GetType().Name}: {ex.Message}");
            }
        };

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = Untrusted.Clean(message, MaxMessageLength) }] };
}
