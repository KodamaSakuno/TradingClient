using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Tests;

public class SymbolTests
{
    [Fact]
    public void SpotSymbol_FormatsRawAsBaseSlashQuote()
    {
        var symbol = new SpotSymbol("BTC", "USDT");

        Assert.Equal("BTC/USDT", symbol.Raw);
        Assert.Equal(ProductKind.Spot, symbol.Product);
    }

    [Fact]
    public void PerpetualFuturesSymbol_MarksKindAndProduct()
    {
        var symbol = new PerpetualFuturesSymbol("BTC", "USDT");

        Assert.Equal("BTC/USDT:PERP", symbol.Raw);
        Assert.Equal(ProductKind.Futures, symbol.Product);
        Assert.Equal(ContractKind.Perpetual, symbol.Kind);
    }

    [Fact]
    public void DeliveryFuturesSymbol_CarriesExpiry()
    {
        var symbol = new DeliveryFuturesSymbol("BTC", "USDT", new DateOnly(2026, 9, 25));

        Assert.Equal("BTC/USDT:2026-09-25", symbol.Raw);
        Assert.Equal(ProductKind.Futures, symbol.Product);
        Assert.Equal(ContractKind.Delivery, symbol.Kind);
        Assert.Equal(new DateOnly(2026, 9, 25), symbol.Expiry);
    }

    [Fact]
    public void OptionSymbol_CarriesFourSemanticComponents()
    {
        var symbol = new OptionSymbol("BTC", new DateOnly(2026, 9, 25), 100_000m, OptionRight.Call);

        Assert.Equal("BTC-2026-09-25-100000-Call", symbol.Raw);
        Assert.Equal(ProductKind.Options, symbol.Product);
        Assert.Equal(100_000m, symbol.Strike);
        Assert.Equal(OptionRight.Call, symbol.Right);
    }

    [Fact]
    public void Symbols_WithSameSemantics_AreEqual()
    {
        Assert.Equal(new SpotSymbol("BTC", "USDT"), new SpotSymbol("BTC", "USDT"));
        Assert.NotEqual(new SpotSymbol("BTC", "USDT"), new SpotSymbol("ETH", "USDT"));
        Assert.NotEqual<Symbol>(
            new SpotSymbol("BTC", "USDT"),
            new PerpetualFuturesSymbol("BTC", "USDT"));
    }
}
