using TradingClient.Exchanges.Common;

namespace TradingClient.Infrastructure.Tests;

public class ServerTimeSyncTests
{
    [Fact]
    public void UtcNow_BeforeUpdate_TracksLocalClock()
    {
        var sync = new ServerTimeSync();

        Assert.Equal(DateTimeOffset.UtcNow, sync.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void UtcNow_AfterUpdate_AppliesServerOffset()
    {
        var sync = new ServerTimeSync();
        var serverAhead = DateTimeOffset.UtcNow.AddSeconds(5);

        sync.Update(serverAhead);

        Assert.Equal(DateTimeOffset.UtcNow.AddSeconds(5), sync.UtcNow, TimeSpan.FromSeconds(2));
    }
}
