namespace TradingClient.Exchanges.Common;

public sealed class TokenBucketRateLimiter
{
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private readonly object _gate = new();
    private double _tokens;
    private DateTimeOffset _lastRefill;

    public TokenBucketRateLimiter(double capacity, double refillPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(refillPerSecond);
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _tokens = capacity;
        _lastRefill = DateTimeOffset.UtcNow;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_gate)
            {
                Refill();
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }
                delay = TimeSpan.FromSeconds((1 - _tokens) / _refillPerSecond);
            }
            await Task.Delay(delay, ct);
        }
    }

    private void Refill()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0)
            return;
        _tokens = Math.Min(_capacity, _tokens + elapsed * _refillPerSecond);
        _lastRefill = now;
    }
}
