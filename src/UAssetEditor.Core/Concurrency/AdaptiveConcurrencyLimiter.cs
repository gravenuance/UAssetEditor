namespace UAssetEditor.Core.Concurrency;

/// <summary>
/// A concurrency limiter whose capacity shrinks and grows over time in response to
/// system memory pressure (<see cref="GC.GetGCMemoryInfo"/>), instead of staying fixed
/// for the whole run. Bounded to <c>[max(1, maxDegree/4), maxDegree]</c>.
/// </summary>
public sealed class AdaptiveConcurrencyLimiter : IDisposable
{
    private const double HighPressureRatio = 0.90;
    private const double ElevatedPressureRatio = 0.75;
    private const double LowPressureRatio = 0.50;
    private static readonly TimeSpan RebalanceInterval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _semaphore;
    private readonly Timer _timer;
    private readonly int _minDegree;
    private readonly int _maxDegree;
    private readonly object _lock = new();
    private int _currentDegree;

    /// <summary>
    /// Permits owed back to the semaphore before the next release actually frees a slot.
    /// SemaphoreSlim has no way to reclaim an already-available permit or shrink its
    /// count directly, so a reduction is applied lazily: the next N releases are
    /// absorbed as debt instead of returned to the pool, which drains real concurrency
    /// down to the new target as in-flight work finishes.
    /// </summary>
    private int _reductionDebt;

    public AdaptiveConcurrencyLimiter(int? maxDegreeOfParallelism = null)
    {
        _maxDegree = Math.Max(1, maxDegreeOfParallelism ?? Environment.ProcessorCount);
        _minDegree = Math.Max(1, _maxDegree / 4);
        _currentDegree = _maxDegree;
        _semaphore = new SemaphoreSlim(_currentDegree, _maxDegree);
        _timer = new Timer(_ => RebalanceCore(GetMemoryLoadRatio()), null, RebalanceInterval, RebalanceInterval);
    }

    public int CurrentDegree
    {
        get { lock (_lock) return _currentDegree; }
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            if (_reductionDebt > 0)
            {
                _reductionDebt--;
                return;
            }
        }

        _semaphore.Release();
    }

    private static double GetMemoryLoadRatio()
    {
        var info = GC.GetGCMemoryInfo();
        return info.HighMemoryLoadThresholdBytes <= 0
            ? 0
            : info.MemoryLoadBytes / (double)info.HighMemoryLoadThresholdBytes;
    }

    internal void RebalanceCore(double memoryLoadRatio)
    {
        lock (_lock)
        {
            var target = memoryLoadRatio switch
            {
                >= HighPressureRatio => _minDegree,
                >= ElevatedPressureRatio => Math.Max(_minDegree, _currentDegree - 1),
                <= LowPressureRatio => Math.Min(_maxDegree, _currentDegree + 1),
                _ => _currentDegree,
            };

            if (target > _currentDegree)
            {
                var increaseBy = target - _currentDegree;
                _currentDegree = target;

                // Growing back up first cancels out any still-outstanding debt from an
                // earlier shrink - those held permits simply flow back to the pool
                // normally once released - before releasing any genuinely new permits.
                var debtRelief = Math.Min(_reductionDebt, increaseBy);
                _reductionDebt -= debtRelief;
                increaseBy -= debtRelief;

                if (increaseBy > 0)
                    _semaphore.Release(increaseBy);
            }
            else if (target < _currentDegree)
            {
                var reduceBy = _currentDegree - target;
                _currentDegree = target;

                // Reclaim as many currently-idle permits as possible right away (a
                // waiting SemaphoreSlim.Wait(0) never blocks); only the remainder - held
                // by work that's still in flight - becomes debt, since there's no way to
                // reclaim a permit that's already been handed out.
                while (reduceBy > 0 && _semaphore.Wait(0))
                    reduceBy--;

                _reductionDebt += reduceBy;
            }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _semaphore.Dispose();
    }

    private sealed class Lease(AdaptiveConcurrencyLimiter owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            owner.Release();
        }
    }
}
