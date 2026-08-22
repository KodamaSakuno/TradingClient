using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Exchanges.ContractTests.Contract;

/// <summary>ISpotTrading 契约测试基类。每个连接器实现按账户模式（Classic / Unified）各派生一个 fixture。</summary>
public abstract class SpotTradingContractTests
{
    protected abstract ISpotTrading CreateConnector();

    [Fact]
    public async Task PlaceSpotOrderAsync_WithValidLimitOrder_ReturnsOrder()
    {
        var connector = CreateConnector();
        var symbol = new SpotSymbol("BTC", "USDT");

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(symbol, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(string.IsNullOrEmpty(result.Value!.OrderId));
        Assert.Equal(symbol, result.Value.Symbol);
        Assert.Equal(OrderSide.Buy, result.Value.Side);
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithInvalidQuantity_ReturnsValidationError()
    {
        var connector = CreateConnector();

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(new SpotSymbol("BTC", "USDT"), OrderSide.Buy, OrderType.Limit, 50_000m, 0m),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task CancelSpotOrderAsync_WithUnknownOrder_ReturnsFailure()
    {
        var connector = CreateConnector();

        var result = await connector.CancelSpotOrderAsync(
            new SpotSymbol("BTC", "USDT"), "no-such-order",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task SpotOrderUpdates_EmitsUpdateForPlacedOrder()
    {
        var connector = CreateConnector();
        var updateTask = connector.SpotOrderUpdates
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(5))
            .ToTask(TestContext.Current.CancellationToken);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(new SpotSymbol("BTC", "USDT"), OrderSide.Sell, OrderType.Limit, 60_000m, 0.01m),
            TestContext.Current.CancellationToken);

        var update = await updateTask;
        Assert.Equal(result.Value!.OrderId, update.Order.OrderId);
    }
}
