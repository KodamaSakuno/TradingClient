namespace TradingClient.Exchanges.Common;

/// <summary>维护与交易所服务器时间的偏移，避免签名时间戳被拒。</summary>
public sealed class ServerTimeSync
{
    private readonly object _gate = new();
    private TimeSpan _offset;

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
                return DateTimeOffset.UtcNow + _offset;
        }
    }

    public void Update(DateTimeOffset serverTime)
    {
        lock (_gate)
            _offset = serverTime - DateTimeOffset.UtcNow;
    }
}
