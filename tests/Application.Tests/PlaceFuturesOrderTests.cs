using TradingClient.Application.Abstractions;
using TradingClient.Application.Risk;
using TradingClient.Application.Services;
using TradingClient.Application.Tests.Fakes;
using TradingClient.Application.UseCases.Futures;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests;

public class PlaceFuturesOrderTests
{
    private static readonly PerpetualFuturesSymbol BtcUsdtPerp = new("BTC", "USDT");

    private static Instrument Instrument(InstrumentStatus status = InstrumentStatus.Trading) =>
        new(BtcUsdtPerp, TickSize: 0.01m, StepSize: 0.001m, MinQuantity: 0.001m, null, null, status);

    // 默认空规则链：全部放行
    private static (PlaceFuturesOrder UseCase, FakeFuturesTrading Trading) CreateUseCase(
        InstrumentStatus status = InstrumentStatus.Trading, PreTradeRiskChain? riskChain = null)
    {
        var marketData = new FakeMarketData();
        marketData.SetInstruments(ProductKind.Futures, Instrument(status));
        var trading = new FakeFuturesTrading();
        return (new PlaceFuturesOrder(
            trading, new InstrumentCache(marketData),
            riskChain ?? new PreTradeRiskChain([], new FakeRiskAuditSink())), trading);
    }

    private static PlaceFuturesOrderRequest LimitRequest(decimal price, decimal quantity) =>
        new(BtcUsdtPerp, OrderSide.Buy, OrderType.Limit, price, quantity,
            PositionSide.Both, MarginMode.Cross, Leverage: null);

    [Fact]
    public async Task ExecuteAsync_LimitOrder_AlignsPriceAndQuantityBeforePlacing()
    {
        var (useCase, trading) = CreateUseCase();

        var result = await useCase.ExecuteAsync(LimitRequest(1.23456m, 1.23456m), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1.23m, trading.LastPlaceRequest!.Price);
        Assert.Equal(1.234m, trading.LastPlaceRequest.Quantity);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownSymbol_ReturnsUnknownInstrumentWithoutCallingGateway()
    {
        var (useCase, trading) = CreateUseCase();
        var request = new PlaceFuturesOrderRequest(
            new PerpetualFuturesSymbol("ETH", "USDT"), OrderSide.Buy, OrderType.Limit, 1.23m, 0.5m,
            PositionSide.Both, MarginMode.Cross, Leverage: null);

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("UNKNOWN_INSTRUMENT", result.Error!.Code);
        Assert.Equal(0, trading.PlaceCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_RiskRejects_ReturnsFailureWithoutCallingGateway()
    {
        var audit = new FakeRiskAuditSink();
        var chain = new PreTradeRiskChain(
            [new StubRiskRule("ConnectionGuard", new RiskRejection("ConnectionGuard", "NOT_CONNECTED", "down"))],
            audit);
        var (useCase, trading) = CreateUseCase(riskChain: chain);

        var result = await useCase.ExecuteAsync(LimitRequest(1.23m, 0.5m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_CONNECTED", result.Error!.Code);
        Assert.Equal(0, trading.PlaceCallCount);
        Assert.Single(audit.Records);
    }

    [Fact]
    public async Task ExecuteAsync_GatewaySuccess_NotifiesOrderPlacedHooks()
    {
        var hook = new StubHookRiskRule();
        var (useCase, _) = CreateUseCase(
            riskChain: new PreTradeRiskChain([hook], new FakeRiskAuditSink()));

        var result = await useCase.ExecuteAsync(LimitRequest(1.23m, 0.5m), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, hook.HookCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ValidOrder_ReturnsGatewayOrder()
    {
        var (useCase, trading) = CreateUseCase();
        var order = new FuturesOrder(
            "GATE-42", BtcUsdtPerp, OrderSide.Buy, OrderType.Limit, 1.23m, 0.5m,
            FilledQuantity: 0m, OrderStatus.New, PositionSide.Both, MarginMode.Cross, DateTimeOffset.UtcNow);
        trading.NextPlaceResult = Result.Success(order);

        var result = await useCase.ExecuteAsync(LimitRequest(1.23m, 0.5m), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(order, result.Value);
    }
}
