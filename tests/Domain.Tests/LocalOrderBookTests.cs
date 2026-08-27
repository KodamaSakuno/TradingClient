using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Domain.Tests;

public class LocalOrderBookTests
{
    private static readonly SpotSymbol BtcUsdt = new("BTC", "USDT");

    private static OrderBookDelta Snapshot(
        IReadOnlyList<OrderBookLevel> bids, IReadOnlyList<OrderBookLevel> asks) =>
        new(BtcUsdt, bids, asks, IsSnapshot: true, DateTimeOffset.UtcNow);

    private static OrderBookDelta Delta(
        IReadOnlyList<OrderBookLevel> bids, IReadOnlyList<OrderBookLevel> asks) =>
        new(BtcUsdt, bids, asks, IsSnapshot: false, DateTimeOffset.UtcNow);

    [Fact]
    public void Apply_WithSnapshot_RebuildsBothSides()
    {
        var book = new LocalOrderBook();

        book.Apply(Snapshot(
            [new OrderBookLevel(100m, 1m), new OrderBookLevel(99m, 2m)],
            [new OrderBookLevel(101m, 3m), new OrderBookLevel(102m, 4m)]));

        Assert.Equal(new OrderBookLevel(100m, 1m), book.BestBid);
        Assert.Equal(new OrderBookLevel(101m, 3m), book.BestAsk);
        Assert.Equal(2, book.GetTop(OrderSide.Buy, 10).Count);
        Assert.Equal(2, book.GetTop(OrderSide.Sell, 10).Count);
    }

    [Fact]
    public void Apply_WithSnapshotAfterIncremental_DiscardsOldLevels()
    {
        var book = new LocalOrderBook();
        book.Apply(Snapshot([new OrderBookLevel(100m, 1m)], [new OrderBookLevel(101m, 1m)]));
        book.Apply(Delta([new OrderBookLevel(99m, 5m)], []));

        book.Apply(Snapshot([new OrderBookLevel(50m, 1m)], [new OrderBookLevel(55m, 1m)]));

        Assert.Equal(new OrderBookLevel(50m, 1m), book.BestBid);
        Assert.Equal(new OrderBookLevel(55m, 1m), book.BestAsk);
        Assert.Single(book.GetTop(OrderSide.Buy, 10));
        Assert.Single(book.GetTop(OrderSide.Sell, 10));
    }

    [Fact]
    public void Apply_WithIncremental_UpsertsLevels()
    {
        var book = new LocalOrderBook();
        book.Apply(Snapshot([new OrderBookLevel(100m, 1m)], [new OrderBookLevel(101m, 1m)]));

        // 已有价位更新数量 + 新价位插入
        book.Apply(Delta([new OrderBookLevel(100m, 9m), new OrderBookLevel(99m, 2m)], []));

        Assert.Equal(new OrderBookLevel(100m, 9m), book.BestBid);
        Assert.Equal(2, book.GetTop(OrderSide.Buy, 10).Count);
    }

    [Fact]
    public void Apply_WithZeroQuantity_RemovesLevel()
    {
        var book = new LocalOrderBook();
        book.Apply(Snapshot(
            [new OrderBookLevel(100m, 1m), new OrderBookLevel(99m, 2m)],
            [new OrderBookLevel(101m, 3m)]));

        book.Apply(Delta([new OrderBookLevel(100m, 0m)], []));

        Assert.Equal(new OrderBookLevel(99m, 2m), book.BestBid);
        Assert.Single(book.GetTop(OrderSide.Buy, 10));
    }

    [Fact]
    public void Apply_WithZeroQuantityOnMissingLevel_IsNoOp()
    {
        var book = new LocalOrderBook();
        book.Apply(Snapshot([new OrderBookLevel(100m, 1m)], []));

        // 删除不存在的价位：适配器乱序/重复推送下的幂等语义
        book.Apply(Delta([new OrderBookLevel(98m, 0m)], []));

        Assert.Single(book.GetTop(OrderSide.Buy, 10));
    }

    [Fact]
    public void Apply_WithRepeatedDelta_IsIdempotent()
    {
        var book = new LocalOrderBook();
        var delta = Delta([new OrderBookLevel(100m, 5m)], [new OrderBookLevel(101m, 6m)]);

        book.Apply(delta);
        book.Apply(delta);

        Assert.Equal(new OrderBookLevel(100m, 5m), book.BestBid);
        Assert.Equal(new OrderBookLevel(101m, 6m), book.BestAsk);
        Assert.Single(book.GetTop(OrderSide.Buy, 10));
        Assert.Single(book.GetTop(OrderSide.Sell, 10));
    }

    [Fact]
    public void GetTop_ReturnsBidsDescendingAsksAscending()
    {
        var book = new LocalOrderBook();
        // 刻意乱序放入，验证排序由维护器保证而非输入顺序
        book.Apply(Snapshot(
            [new OrderBookLevel(98m, 1m), new OrderBookLevel(100m, 1m), new OrderBookLevel(99m, 1m)],
            [new OrderBookLevel(102m, 1m), new OrderBookLevel(101m, 1m), new OrderBookLevel(103m, 1m)]));

        Assert.Equal([100m, 99m, 98m], book.GetTop(OrderSide.Buy, 10).Select(l => l.Price).ToArray());
        Assert.Equal([101m, 102m, 103m], book.GetTop(OrderSide.Sell, 10).Select(l => l.Price).ToArray());
    }

    [Fact]
    public void GetTop_WithDepthBelowLevelCount_TruncatesToBestLevels()
    {
        var book = new LocalOrderBook();
        book.Apply(Snapshot(
            [new OrderBookLevel(100m, 1m), new OrderBookLevel(99m, 1m), new OrderBookLevel(98m, 1m)],
            [new OrderBookLevel(101m, 1m), new OrderBookLevel(102m, 1m), new OrderBookLevel(103m, 1m)]));

        Assert.Equal([100m, 99m], book.GetTop(OrderSide.Buy, 2).Select(l => l.Price).ToArray());
        Assert.Equal([101m, 102m], book.GetTop(OrderSide.Sell, 2).Select(l => l.Price).ToArray());
    }

    [Fact]
    public void BestBidAsk_WithEmptyBook_ReturnNull()
    {
        var book = new LocalOrderBook();

        Assert.Null(book.BestBid);
        Assert.Null(book.BestAsk);
        Assert.Empty(book.GetTop(OrderSide.Buy, 10));
        Assert.Empty(book.GetTop(OrderSide.Sell, 10));
    }

    [Fact]
    public void Apply_WithEmptySnapshot_ClearsBook()
    {
        var book = new LocalOrderBook();
        book.Apply(Snapshot([new OrderBookLevel(100m, 1m)], [new OrderBookLevel(101m, 1m)]));

        book.Apply(Snapshot([], []));

        Assert.Null(book.BestBid);
        Assert.Null(book.BestAsk);
    }
}
