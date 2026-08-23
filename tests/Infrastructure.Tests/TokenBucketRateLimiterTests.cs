using System.Diagnostics;
using TradingClient.Exchanges.Common;

namespace TradingClient.Infrastructure.Tests;

public class TokenBucketRateLimiterTests
{
    [Fact]
    public async Task WaitAsync_WithinCapacity_ReturnsImmediately()
    {
        var limiter = new TokenBucketRateLimiter(capacity: 3, refillPerSecond: 10);

        var elapsed = await TimeAsync(async () =>
        {
            for (var i = 0; i < 3; i++)
                await limiter.WaitAsync(TestContext.Current.CancellationToken);
        });

        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"Expected immediate, took {elapsed}");
    }

    [Fact]
    public async Task WaitAsync_BeyondCapacity_WaitsForRefill()
    {
        var limiter = new TokenBucketRateLimiter(capacity: 1, refillPerSecond: 10);

        await limiter.WaitAsync(TestContext.Current.CancellationToken);
        var elapsed = await TimeAsync(() => limiter.WaitAsync(TestContext.Current.CancellationToken));

        Assert.True(elapsed >= TimeSpan.FromMilliseconds(70), $"Expected ~100ms wait, took {elapsed}");
    }

    [Fact]
    public void Constructor_WithInvalidArguments_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketRateLimiter(0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucketRateLimiter(1, 0));
    }

    private static async Task<TimeSpan> TimeAsync(Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        await action();
        return sw.Elapsed;
    }
}
