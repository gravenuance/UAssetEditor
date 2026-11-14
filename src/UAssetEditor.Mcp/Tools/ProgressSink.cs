using ModelContextProtocol;

namespace UAssetEditor.Mcp.Tools;

/// <summary>An <see cref="IProgress{T}"/> that runs its callback on the reporting thread, unlike <see cref="Progress{T}"/>, which posts it.</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Forwards a long operation's progress to the client's progress token. About a hundred updates are sent per
/// run however many items it has, and the last item always is. Reports run on the calling thread, in order.</summary>
internal sealed class ProgressSink(IProgress<ProgressNotificationValue>? target, string what) : IProgress<(int Done, int Total)>
{
    public void Report((int Done, int Total) value)
    {
        if (target == null || value.Total <= 0) return;

        var step = Math.Max(1, value.Total / 100);
        if (value.Done != value.Total && value.Done % step != 0) return;

        target.Report(new ProgressNotificationValue
        {
            Progress = value.Done,
            Total = value.Total,
            Message = $"{what}: {value.Done} of {value.Total}",
        });
    }
}
