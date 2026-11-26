using System.Text.Json;
using System.Text.RegularExpressions;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Keeps AES keys out of results and logs. <c>RetocProcess</c> already redacts the key it passed, so this is the
/// second gate: every failure text passes through here before it is returned or written, whatever layer produced it.</summary>
internal static partial class Secrets
{
    public const string Redacted = "[redacted]";

    /// <summary>The name of the tool argument that carries a key.</summary>
    public const string KeyArgument = "aesKey";

    // A shorter secret would also match ordinary words; a key that short guards nothing.
    private const int MinSecretLength = 4;

    /// <summary>The text with any <c>--aes-key &lt;value&gt;</c> and any bare 256-bit key (64 hex digits) replaced.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return KeyDigits().Replace(KeyOption().Replace(text, "--aes-key " + Redacted), Redacted);
    }

    /// <summary>Like <see cref="Redact(string?)"/>, and also every spelling of <paramref name="secrets"/> (each with and without a
    /// leading <c>0x</c>, in any case): the key a call was given is redacted whatever shape a message repeats it in.</summary>
    public static string Redact(string? text, IReadOnlyCollection<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        if (string.IsNullOrEmpty(text)) return "";

        var result = text;
        foreach (var spelling in Spellings(secrets))
            result = result.Replace(spelling, Redacted, StringComparison.OrdinalIgnoreCase);
        return Redact(result);
    }

    /// <summary>The key a tool call carries in its arguments, for <see cref="Redact(string?, IReadOnlyCollection{string})"/>.</summary>
    public static IReadOnlyList<string> InArguments(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments == null) return [];

        return [.. arguments
            .Where(pair => string.Equals(pair.Key, KeyArgument, StringComparison.OrdinalIgnoreCase) && pair.Value.ValueKind == JsonValueKind.String)
            .Select(pair => pair.Value.GetString()!)];
    }

    private static IEnumerable<string> Spellings(IReadOnlyCollection<string> secrets)
    {
        var spellings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var secret in secrets.Select(s => s.Trim()))
        {
            if (secret.Length >= MinSecretLength) spellings.Add(secret);
            if (secret.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && secret.Length - 2 >= MinSecretLength) spellings.Add(secret[2..]);
        }
        return spellings.OrderByDescending(s => s.Length);
    }

    [GeneratedRegex(@"--aes-key\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyOption();

    [GeneratedRegex(@"\b(?:0x)?[0-9A-Fa-f]{64}\b", RegexOptions.CultureInvariant)]
    private static partial Regex KeyDigits();
}
