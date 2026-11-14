using System.Text.RegularExpressions;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Keeps AES keys out of results and logs. retoc's failure message repeats its whole command line, and with it
/// the <c>--aes-key</c> it was given, so every failure text passes through here before it is returned or written.</summary>
internal static partial class Secrets
{
    public const string Redacted = "[redacted]";

    /// <summary>The text with any <c>--aes-key &lt;value&gt;</c> and any bare 256-bit key (64 hex digits) replaced.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return KeyDigits().Replace(KeyArgument().Replace(text, "--aes-key " + Redacted), Redacted);
    }

    [GeneratedRegex(@"--aes-key\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyArgument();

    [GeneratedRegex(@"\b(?:0x)?[0-9A-Fa-f]{64}\b", RegexOptions.CultureInvariant)]
    private static partial Regex KeyDigits();
}
