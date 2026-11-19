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
                text = $"{text}: {inner.Message}";
        }
        return text;
    }
}
