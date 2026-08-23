using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Tests;

public class InstrumentTests
{
    private static Instrument BtcUsdtSpot() => new(
        new SpotSymbol("BTC", "USDT"),
        TickSize: 0.01m,
        StepSize: 0.0001m,
        MinQuantity: 0.0001m,
        ContractMultiplier: null,
        Status: InstrumentStatus.Trading);

    public static TheoryData<Symbol, ProductKind> SymbolsWithProduct => new()
    {
        { new SpotSymbol("BTC", "USDT"), ProductKind.Spot },
        { new PerpetualFuturesSymbol("BTC", "USDT"), ProductKind.Futures },
        { new DeliveryFuturesSymbol("BTC", "USDT", new DateOnly(2026, 9, 25)), ProductKind.Futures },
        { new OptionSymbol("BTC", new DateOnly(2026, 9, 25), 100_000m, OptionRight.Call), ProductKind.Options },
    };

    [Theory]
    [MemberData(nameof(SymbolsWithProduct))]
    public void Product_IsDerivedFromSymbolSubtype(Symbol symbol, ProductKind expected)
    {
        var instrument = new Instrument(symbol, 0.01m, 0.0001m, 0.0001m, null, InstrumentStatus.Trading);

        Assert.Equal(expected, instrument.Product);
    }

    [Fact]
    public void AlignPrice_RoundsDownToTickSize()
    {
        Assert.Equal(50_000.01m, BtcUsdtSpot().AlignPrice(50_000.019m));
    }

    [Fact]
    public void AlignQuantity_RoundsDownToStepSize()
    {
        Assert.Equal(0.1234m, BtcUsdtSpot().AlignQuantity(0.12345m));
    }

    [Fact]
    public void ValidateOrder_WithValidLimitOrder_Succeeds()
    {
        var result = BtcUsdtSpot().ValidateOrder(50_000.01m, 0.01m);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ValidateOrder_WithMarketOrder_SkipsPriceCheck()
    {
        var result = BtcUsdtSpot().ValidateOrder(price: null, 0.01m);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ValidateOrder_WithUnalignedPrice_ReturnsError()
    {
        var result = BtcUsdtSpot().ValidateOrder(50_000.019m, 0.01m);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_PRICE", result.Error?.Code);
    }

    [Fact]
    public void ValidateOrder_WithQuantityBelowMinimum_ReturnsError()
    {
        var result = BtcUsdtSpot().ValidateOrder(50_000m, 0.00001m);

        Assert.False(result.IsSuccess);
        Assert.Equal("QUANTITY_TOO_SMALL", result.Error?.Code);
    }

    [Fact]
    public void ValidateOrder_WithUnalignedQuantity_ReturnsError()
    {
        var result = BtcUsdtSpot().ValidateOrder(50_000m, 0.00015m);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_QUANTITY", result.Error?.Code);
    }

    [Fact]
    public void ValidateOrder_WithSuspendedInstrument_ReturnsError()
    {
        var suspended = BtcUsdtSpot() with { Status = InstrumentStatus.Suspended };

        var result = suspended.ValidateOrder(50_000m, 0.01m);

        Assert.False(result.IsSuccess);
        Assert.Equal("INSTRUMENT_NOT_TRADING", result.Error?.Code);
    }
}
