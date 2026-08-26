using TradingClient.Application.Services;
using TradingClient.Application.Tests.Fakes;
using TradingClient.Domain.Instruments;

namespace TradingClient.Application.Tests;

public class InstrumentCacheTests
{
    private static readonly SpotSymbol BtcUsdt = new("BTC", "USDT");
    private static readonly PerpetualFuturesSymbol BtcPerp = new("BTC", "USDT");

    private static Instrument SpotInstrument(Symbol? symbol = null, InstrumentStatus status = InstrumentStatus.Trading) =>
        new(symbol ?? BtcUsdt, 0.01m, 0.001m, 0.001m, null, status);

    [Fact]
    public async Task GetAsync_KnownSymbol_ReturnsInstrument()
    {
        var marketData = new FakeMarketData();
        var instrument = SpotInstrument();
        marketData.SetInstruments(ProductKind.Spot, instrument);
        var cache = new InstrumentCache(marketData);

        var found = await cache.GetAsync(BtcUsdt, CancellationToken.None);

        Assert.Equal(instrument, found);
    }

    [Fact]
    public async Task GetAsync_UnknownSymbol_ReturnsNull()
    {
        var marketData = new FakeMarketData();
        marketData.SetInstruments(ProductKind.Spot, SpotInstrument());
        var cache = new InstrumentCache(marketData);

        var found = await cache.GetAsync(new SpotSymbol("ETH", "USDT"), CancellationToken.None);

        Assert.Null(found);
        Assert.Equal(1, marketData.CallCount(ProductKind.Spot));
    }

    [Fact]
    public async Task GetAsync_RepeatedCalls_LoadOnlyOnce()
    {
        var marketData = new FakeMarketData();
        marketData.SetInstruments(ProductKind.Spot, SpotInstrument());
        var cache = new InstrumentCache(marketData);

        await cache.GetAsync(BtcUsdt, CancellationToken.None);
        await cache.GetAsync(BtcUsdt, CancellationToken.None);

        Assert.Equal(1, marketData.CallCount(ProductKind.Spot));
    }

    [Fact]
    public async Task GetAsync_ConcurrentFirstCalls_LoadOnlyOnce()
    {
        var marketData = new FakeMarketData { LoadDelay = TimeSpan.FromMilliseconds(50) };
        marketData.SetInstruments(ProductKind.Spot, SpotInstrument());
        var cache = new InstrumentCache(marketData);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => cache.GetAsync(BtcUsdt, CancellationToken.None)));

        Assert.Equal(1, marketData.CallCount(ProductKind.Spot));
        Assert.All(results, Assert.NotNull);
    }

    [Fact]
    public async Task GetAsync_DifferentProducts_LoadSeparately()
    {
        var marketData = new FakeMarketData();
        marketData.SetInstruments(ProductKind.Spot, SpotInstrument());
        var cache = new InstrumentCache(marketData);

        await cache.GetAsync(BtcUsdt, CancellationToken.None);
        var futuresResult = await cache.GetAsync(BtcPerp, CancellationToken.None);

        Assert.Equal(1, marketData.CallCount(ProductKind.Spot));
        Assert.Equal(1, marketData.CallCount(ProductKind.Futures));
        Assert.Null(futuresResult);
    }

    [Fact]
    public async Task RefreshAsync_AfterInitialLoad_ReloadsInstruments()
    {
        var marketData = new FakeMarketData();
        marketData.SetInstruments(ProductKind.Spot, SpotInstrument());
        var cache = new InstrumentCache(marketData);
        await cache.GetAsync(BtcUsdt, CancellationToken.None);

        var updated = SpotInstrument(status: InstrumentStatus.Suspended);
        marketData.SetInstruments(ProductKind.Spot, updated);
        await cache.RefreshAsync(ProductKind.Spot, CancellationToken.None);

        Assert.Equal(2, marketData.CallCount(ProductKind.Spot));
        Assert.Equal(InstrumentStatus.Suspended, (await cache.GetAsync(BtcUsdt, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task RefreshAsync_RemovesDelistedInstruments()
    {
        var marketData = new FakeMarketData();
        marketData.SetInstruments(ProductKind.Spot, SpotInstrument());
        var cache = new InstrumentCache(marketData);
        await cache.GetAsync(BtcUsdt, CancellationToken.None);

        marketData.SetInstruments(ProductKind.Spot);
        await cache.RefreshAsync(ProductKind.Spot, CancellationToken.None);

        Assert.Null(await cache.GetAsync(BtcUsdt, CancellationToken.None));
    }
}
