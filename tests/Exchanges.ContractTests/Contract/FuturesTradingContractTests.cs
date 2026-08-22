using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Exchanges.ContractTests.Contract;

/// <summary>IFuturesTrading 契约测试基类。每个连接器实现按账户模式（Classic / Unified）各派生一个 fixture。</summary>
public abstract class FuturesTradingContractTests
{
    protected abstract IFuturesTrading CreateConnector();

    [Fact]
    public async Task PlaceFuturesOrderAsync_WithValidRequest_ReturnsOrder()
    {
        var connector = CreateConnector();
        var symbol = new PerpetualFuturesSymbol("BTC", "USDT");

        var result = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(
                symbol, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m,
                PositionSide.Long, MarginMode.Cross, Leverage: null),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(string.IsNullOrEmpty(result.Value!.OrderId));
        Assert.Equal(symbol, result.Value.Symbol);
        Assert.Equal(PositionSide.Long, result.Value.PositionSide);
    }

    [Fact]
    public async Task GetPositionsAsync_ReturnsPositionList()
    {
        var connector = CreateConnector();

        var result = await connector.GetPositionsAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
    }

    [Fact]
    public async Task PositionUpdates_CarryPositionSide()
    {
        var connector = CreateConnector();
        var updateTask = connector.PositionUpdates
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(5))
            .ToTask(TestContext.Current.CancellationToken);

        await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(
                new PerpetualFuturesSymbol("BTC", "USDT"), OrderSide.Sell, OrderType.Market, null, 0.01m,
                PositionSide.Short, MarginMode.Isolated, Leverage: null),
            TestContext.Current.CancellationToken);

        var update = await updateTask;
        Assert.Equal(PositionSide.Short, update.Position.Side);
    }
}
