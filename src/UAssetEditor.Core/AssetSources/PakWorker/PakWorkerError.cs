namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>The text the worker sends back for a failed request.</summary>
public static class PakWorkerError
{
    /// <summary>The exception's message followed by those of its inner exceptions, so a stream's real failure reaches the user.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var text = exception.Message;
        for (var inner = exception.InnerException; inner != null; inner = inner.InnerException)
        {
            if (!text.Contains(inner.Message, StringComparison.Ordinal))
                text = $"{WithoutOneTrailingFullStop(text)}: {inner.Message}";
        }
        return text;
    }

    /// <summary>Drops a single closing '.', so an ellipsis or an abbreviation in a message keeps the rest of its dots.</summary>
    private static string WithoutOneTrailingFullStop(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed is [.., '.'] ? trimmed[..^1] : trimmed;
    }
}
