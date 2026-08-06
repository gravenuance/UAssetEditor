using UAssetEditor.Core.Concurrency;

namespace UAssetEditor.Core.Tests;

public class AdaptiveConcurrencyLimiterTests
{
    [Fact]
    public void CurrentDegree_StartsAtMaxDegreeOfParallelism()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(4);

        Assert.Equal(4, limiter.CurrentDegree);
    }

    [Fact]
    public async Task AcquireAsync_BlocksOnceCapacityIsExhaustedAndUnblocksOnRelease()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(2);

        var lease1 = await limiter.AcquireAsync();
        var lease2 = await limiter.AcquireAsync();

        var thirdAcquire = limiter.AcquireAsync();
        await Task.Delay(50);
        Assert.False(thirdAcquire.IsCompleted);

        lease1.Dispose();
        var lease3 = await thirdAcquire;

        Assert.True(thirdAcquire.IsCompleted);
        lease2.Dispose();
        lease3.Dispose();
    }

    [Fact]
    public void RebalanceCore_HighPressure_DropsStraightToMinimum()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(8);

        limiter.RebalanceCore(0.95);

        Assert.Equal(2, limiter.CurrentDegree); // max(1, 8/4)
    }

    [Fact]
    public void RebalanceCore_ElevatedPressure_ShrinksByOnePerCall()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(8);

        limiter.RebalanceCore(0.80);

        Assert.Equal(7, limiter.CurrentDegree);
    }

    [Fact]
    public void RebalanceCore_LowPressure_GrowsByOnePerCallUpToMax()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(8);
        limiter.RebalanceCore(0.95); // drop to min (2) first

        limiter.RebalanceCore(0.10);

        Assert.Equal(3, limiter.CurrentDegree);
    }

    [Fact]
    public void RebalanceCore_NeverExceedsMaxDegree()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(4);

        for (var i = 0; i < 10; i++)
            limiter.RebalanceCore(0.10);

        Assert.Equal(4, limiter.CurrentDegree);
    }

    [Fact]
    public async Task RebalanceCore_Reduction_IsAppliedLazilyAsHeldLeasesAreReleased()
    {
        using var limiter = new AdaptiveConcurrencyLimiter(4);

        var leases = new List<IDisposable>();
        for (var i = 0; i < 4; i++)
            leases.Add(await limiter.AcquireAsync());

        limiter.RebalanceCore(0.95); // target = min = 1, while all 4 permits are held
        Assert.Equal(1, limiter.CurrentDegree);

        foreach (var lease in leases)
            lease.Dispose();

        // Only one permit should have actually made it back to the semaphore.
        var first = await limiter.AcquireAsync();
        var second = limiter.AcquireAsync();
        await Task.Delay(50);

        Assert.False(second.IsCompleted);
        first.Dispose();
    }
}
