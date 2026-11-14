using System.Globalization;
using ModelContextProtocol;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Bounds checks for tool arguments; failures go back to the caller as short MCP errors.</summary>
internal static class ToolInput
{
    public static int InRange(int value, int min, int max, string name) =>
        value >= min && value <= max
            ? value
            : throw new McpException(string.Create(CultureInfo.InvariantCulture, $"{name} must be between {min} and {max}."));

    public static string Required(string? value, string name, int maxLength = 1000)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new McpException($"{name} is required.");

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : throw new McpException(string.Create(CultureInfo.InvariantCulture, $"{name} is limited to {maxLength} characters."));
    }

    /// <summary>The trimmed value, or null when it is missing or blank.</summary>
    public static string? Optional(string? value, string name, int maxLength = 1000) =>
        string.IsNullOrWhiteSpace(value) ? null : Required(value, name, maxLength);

    /// <summary>The named member of an enum, ignoring case; a number is refused so only the listed names are accepted.</summary>
    public static T Name<T>(string text, string name)
        where T : struct, Enum =>
        text.Length > 0 && !char.IsDigit(text[0]) && Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new McpException($"{name} must be one of: {string.Join(", ", Enum.GetNames<T>())}.");

    /// <summary>A case-insensitive name from a fixed list; a number is refused so only the listed names are accepted.</summary>
    public static string OneOf(string? value, string name, params string[] allowed)
    {
        var text = value?.Trim() ?? "";
        return allowed.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"{name} must be one of: {string.Join(", ", allowed)}.");
    }
}
