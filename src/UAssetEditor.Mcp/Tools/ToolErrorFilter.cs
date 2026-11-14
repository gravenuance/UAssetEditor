using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UAssetEditor.Core.AssetSources.IoStore;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Turns tool failures into error results the caller can read: bad input and expected failures by their own
/// message, anything else by its type and message with the stack in the log. No exception reaches the transport, and
/// nothing that reaches the caller or the log carries an AES key (<see cref="Secrets"/>). Cancellation passes through,
/// since the SDK answers it itself.</summary>
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
                    log?.LogInformation("Tool {Tool} returned an error: {Reason}", tool, Secrets.Redact(ex.Message));
                    return Error(ex.Message);
                }

                // The exception object is not logged as such: its message may hold a command line with a key.
                log?.LogError("Tool {Tool} failed: {Chain}{NewLine}{Stack}", tool, Secrets.Redact(Chain(ex)), Environment.NewLine, Secrets.Redact(ex.StackTrace));
                var reason = ex is IoStoreConversionException conversion ? conversion.UserMessage : ex.Message;
                return Error($"{tool} failed: {ex.GetType().Name}: {reason}");
            }
        };

    /// <summary>"Type: message", then each inner exception's, outermost first.</summary>
    private static string Chain(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current != null; current = current.InnerException)
            parts.Add($"{current.GetType().FullName}: {current.Message}");
        return string.Join(" <- ", parts);
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = Untrusted.Clean(Secrets.Redact(message), MaxMessageLength) }] };
}
