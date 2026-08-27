using TradingClient.Application.Abstractions;
using TradingClient.Application.Risk;
using TradingClient.Application.Services;
using TradingClient.Application.Tests.Fakes;
using TradingClient.Application.UseCases.Spot;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests;

public class PlaceSpotOrderTests
{
    private static readonly SpotSymbol BtcUsdt = new("BTC", "USDT");

    private static Instrument Instrument(InstrumentStatus status = InstrumentStatus.Trading) =>
        new(BtcUsdt, TickSize: 0.01m, StepSize: 0.001m, MinQuantity: 0.001m, null, null, status);

    // 默认空规则链：全部放行
    private static (PlaceSpotOrder UseCase, FakeSpotTrading Trading) CreateUseCase(
        InstrumentStatus status = InstrumentStatus.Trading, PreTradeRiskChain? riskChain = null)
    {
        var marketData = new FakeMarketData();
        marketData.SetInstruments(ProductKind.Spot, Instrument(status));
        var trading = new FakeSpotTrading();
        return (new PlaceSpotOrder(
            trading, new InstrumentCache(marketData),
            riskChain ?? new PreTradeRiskChain([], new FakeRiskAuditSink())), trading);
    }

    private static PlaceSpotOrderRequest LimitRequest(decimal price, decimal quantity) =>
        new(BtcUsdt, OrderSide.Buy, OrderType.Limit, price, quantity);

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
    public async Task ExecuteAsync_MarketOrder_KeepsNullPrice()
    {
        var (useCase, trading) = CreateUseCase();

        var result = await useCase.ExecuteAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Market, null, 0.5m),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(trading.LastPlaceRequest!.Price);
    }

    [Fact]
    public async Task ExecuteAsync_PriceAlignsToZero_FailsValidationWithoutCallingGateway()
    {
        var (useCase, trading) = CreateUseCase();

        // 0.009 按 tick=0.01 Floor 后为 0，触发 INVALID_PRICE
        var result = await useCase.ExecuteAsync(LimitRequest(0.009m, 0.5m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_PRICE", result.Error!.Code);
        Assert.Equal(0, trading.PlaceCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_QuantityBelowMinimum_FailsValidationWithoutCallingGateway()
    {
        var (useCase, trading) = CreateUseCase();

        // 0.0009 按 step=0.001 Floor 后为 0，低于 MinQuantity
        var result = await useCase.ExecuteAsync(LimitRequest(1.23m, 0.0009m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("QUANTITY_TOO_SMALL", result.Error!.Code);
        Assert.Equal(0, trading.PlaceCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_SuspendedInstrument_ReturnsNotTradingWithoutCallingGateway()
    {
        var (useCase, trading) = CreateUseCase(InstrumentStatus.Suspended);

        var result = await useCase.ExecuteAsync(LimitRequest(1.23m, 0.5m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INSTRUMENT_NOT_TRADING", result.Error!.Code);
        Assert.Equal(0, trading.PlaceCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownSymbol_ReturnsUnknownInstrumentWithoutCallingGateway()
    {
        var (useCase, trading) = CreateUseCase();
        var request = new PlaceSpotOrderRequest(
            new SpotSymbol("ETH", "USDT"), OrderSide.Buy, OrderType.Limit, 1.23m, 0.5m);

        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("UNKNOWN_INSTRUMENT", result.Error!.Code);
        Assert.Equal(0, trading.PlaceCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_GatewayFailure_PropagatesError()
    {
        var (useCase, trading) = CreateUseCase();
        trading.NextPlaceResult = Result.Failure<SpotOrder>(
            new ExchangeError("INSUFFICIENT_BALANCE", "Not enough balance."));

        var result = await useCase.ExecuteAsync(LimitRequest(1.23m, 0.5m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INSUFFICIENT_BALANCE", result.Error!.Code);
        Assert.Equal(1, trading.PlaceCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ValidOrder_ReturnsGatewayOrder()
    {
        var (useCase, trading) = CreateUseCase();
        var order = new SpotOrder(
            "GATE-42", BtcUsdt, OrderSide.Buy, OrderType.Limit, 1.23m, 0.5m,
            FilledQuantity: 0m, OrderStatus.New, DateTimeOffset.UtcNow);
        trading.NextPlaceResult = Result.Success(order);

        var result = await useCase.ExecuteAsync(LimitRequest(1.23m, 0.5m), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(order, result.Value);
    }

    [Fact]
    public async Task ExecuteAsync_RiskRejects_ReturnsFailureWithoutCallingGateway()
    {
        var audit = new FakeRiskAuditSink();
        var chain = new PreTradeRiskChain(
            [new StubRiskRule("OrderSizeLimit", new RiskRejection("OrderSizeLimit", "ORDER_SIZE_EXCEEDED", "too big"))],
            audit);
        var (useCase, trading) = CreateUseCase(riskChain: chain);

        var result = await useCase.ExecuteAsync(LimitRequest(1.23m, 0.5m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("ORDER_SIZE_EXCEEDED", result.Error!.Code);
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
    public async Task ExecuteAsync_GatewayFailure_DoesNotNotifyOrderPlacedHooks()
    {
        var hook = new StubHookRiskRule();
        var (useCase, trading) = CreateUseCase(
            riskChain: new PreTradeRiskChain([hook], new FakeRiskAuditSink()));
        trading.NextPlaceResult = Result.Failure<SpotOrder>(
            new ExchangeError("INSUFFICIENT_BALANCE", "Not enough balance."));

        var result = await useCase.ExecuteAsync(LimitRequest(1.23m, 0.5m), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, hook.HookCallCount);
    }
}
