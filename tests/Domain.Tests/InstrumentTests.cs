using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Tests;

public class InstrumentTests
{
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
        var instrument = new Instrument(symbol);

        Assert.Equal(expected, instrument.Product);
    }
}
