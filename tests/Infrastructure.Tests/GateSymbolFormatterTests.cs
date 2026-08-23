using TradingClient.Domain.Instruments;
using TradingClient.Exchanges.Gate;

namespace TradingClient.Infrastructure.Tests;

public class GateSymbolFormatterTests
{
    [Fact]
    public void FormatSpot_WithSpotSymbol_ReturnsGatePair()
    {
        var result = GateSymbolFormatter.FormatSpot(new SpotSymbol("BTC", "USDT"));

        Assert.Equal("BTC_USDT", result);
    }

    [Fact]
    public void FormatFutures_WithPerpetual_ReturnsGatePair()
    {
        var result = GateSymbolFormatter.FormatFutures(new PerpetualFuturesSymbol("BTC", "USDT"));

        Assert.Equal("BTC_USDT", result);
    }

    [Fact]
    public void FormatFutures_WithDelivery_AppendsExpiryDate()
    {
        var symbol = new DeliveryFuturesSymbol("BTC", "USDT", new DateOnly(2026, 9, 25));

        var result = GateSymbolFormatter.FormatFutures(symbol);

        Assert.Equal("BTC_USDT_20260925", result);
    }

    [Fact]
    public void ParseSpot_WithGatePair_ReturnsSpotSymbol()
    {
        var result = GateSymbolFormatter.ParseSpot("BTC_USDT");

        Assert.Equal(new SpotSymbol("BTC", "USDT"), result);
    }

    [Fact]
    public void ParseFutures_WithTwoSegments_ReturnsPerpetual()
    {
        var result = GateSymbolFormatter.ParseFutures("BTC_USDT");

        var perpetual = Assert.IsType<PerpetualFuturesSymbol>(result);
        Assert.Equal(new PerpetualFuturesSymbol("BTC", "USDT"), perpetual);
    }

    [Fact]
    public void ParseFutures_WithDeliveryDate_ReturnsDelivery()
    {
        var result = GateSymbolFormatter.ParseFutures("BTC_USDT_20260925");

        var delivery = Assert.IsType<DeliveryFuturesSymbol>(result);
        Assert.Equal(new DeliveryFuturesSymbol("BTC", "USDT", new DateOnly(2026, 9, 25)), delivery);
    }

    [Fact]
    public void ParseSpot_WithLowercaseInput_NormalizesToUppercase()
    {
        var result = GateSymbolFormatter.ParseSpot("btc_usdt");

        Assert.Equal(new SpotSymbol("BTC", "USDT"), result);
    }

    [Fact]
    public void ParseFutures_WithLowercaseInput_NormalizesToUppercase()
    {
        var result = GateSymbolFormatter.ParseFutures("btc_usdt_20260925");

        Assert.Equal(new DeliveryFuturesSymbol("BTC", "USDT", new DateOnly(2026, 9, 25)), result);
    }

    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData("BTC_USDT_20261301")] // 2026-13-01 is not a valid date
    [InlineData("BTC_USDT_20260925_EXTRA")]
    [InlineData("BTC__USDT")]
    [InlineData("_USDT")]
    public void ParseFutures_WithInvalidInput_ThrowsArgumentException(string raw)
    {
        Assert.Throws<ArgumentException>(() => GateSymbolFormatter.ParseFutures(raw));
    }

    [Fact]
    public void ParseSpot_WithoutUnderscore_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => GateSymbolFormatter.ParseSpot("BTCUSDT"));
    }

    [Fact]
    public void ParseSpot_WithThreeSegments_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => GateSymbolFormatter.ParseSpot("BTC_USDT_20260925"));
    }

    [Fact]
    public void Roundtrip_SpotSymbol_PreservesSemantics()
    {
        var symbol = new SpotSymbol("BTC", "USDT");

        var parsed = GateSymbolFormatter.ParseSpot(GateSymbolFormatter.FormatSpot(symbol));

        Assert.Equal(symbol, parsed);
    }

    [Fact]
    public void Roundtrip_PerpetualFuturesSymbol_PreservesSemantics()
    {
        var symbol = new PerpetualFuturesSymbol("BTC", "USDT");

        var parsed = GateSymbolFormatter.ParseFutures(GateSymbolFormatter.FormatFutures(symbol));

        Assert.Equal(symbol, parsed);
    }

    [Fact]
    public void Roundtrip_DeliveryFuturesSymbol_PreservesSemantics()
    {
        var symbol = new DeliveryFuturesSymbol("BTC", "USDT", new DateOnly(2026, 9, 25));

        var parsed = GateSymbolFormatter.ParseFutures(GateSymbolFormatter.FormatFutures(symbol));

        Assert.Equal(symbol, parsed);
    }
}
