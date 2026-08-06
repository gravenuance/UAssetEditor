namespace UAssetEditor.Core.Concurrency;

/// <summary>
/// Runs <paramref name="body"/> once per item, throttled either to a fixed degree of
/// parallelism (when <paramref name="maxDegreeOfParallelism"/> is a positive override)
/// or to an <see cref="AdaptiveConcurrencyLimiter"/> that responds to memory pressure
/// (the default). Unlike <see cref="Parallel.ForEachAsync{TSource}(System.Collections.Generic.IEnumerable{TSource},ParallelOptions,Func{TSource,CancellationToken,ValueTask})"/>,
/// the adaptive path can change its effective concurrency mid-run.
/// </summary>
public static class ThrottledParallel
{
    public static async Task ForEachAsync<T>(
        IReadOnlyList<T> items,
        int? maxDegreeOfParallelism,
        Func<T, CancellationToken, Task> body,
        CancellationToken cancellationToken)
    {
        if (maxDegreeOfParallelism is > 0)
        {
            using var fixedLimiter = new SemaphoreSlim(maxDegreeOfParallelism.Value, maxDegreeOfParallelism.Value);
            await Task.WhenAll(items.Select(async item =>
            {
                await fixedLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await body(item, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    fixedLimiter.Release();
                }
            })).ConfigureAwait(false);
            return;
        }

        using var limiter = new AdaptiveConcurrencyLimiter();
        await Task.WhenAll(items.Select(async item =>
        {
            using var lease = await limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
            await body(item, cancellationToken).ConfigureAwait(false);
        })).ConfigureAwait(false);
    }
}
