using TradingClient.Application.Abstractions;
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
        new(BtcUsdt, TickSize: 0.01m, StepSize: 0.001m, MinQuantity: 0.001m, null, status);

    private static (PlaceSpotOrder UseCase, FakeSpotTrading Trading) CreateUseCase(
        InstrumentStatus status = InstrumentStatus.Trading)
    {
        var marketData = new FakeMarketData();
        marketData.SetInstruments(ProductKind.Spot, Instrument(status));
        var trading = new FakeSpotTrading();
        return (new PlaceSpotOrder(trading, new InstrumentCache(marketData)), trading);
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
}
