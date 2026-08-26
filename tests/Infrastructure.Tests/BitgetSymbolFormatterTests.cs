using TradingClient.Domain.Instruments;
using TradingClient.Exchanges.Bitget;

namespace TradingClient.Infrastructure.Tests;

public class BitgetSymbolFormatterTests
{
    [Fact]
    public void FormatSpot_WithSpotSymbol_ReturnsBitgetPair()
    {
        var result = BitgetSymbolFormatter.FormatSpot(new SpotSymbol("BTC", "USDT"));

        Assert.Equal("BTCUSDT", result);
    }

    [Fact]
    public void FormatSpot_WithLowercaseInput_NormalizesToUppercase()
    {
        var result = BitgetSymbolFormatter.FormatSpot(new SpotSymbol("btc", "usdt"));

        Assert.Equal("BTCUSDT", result);
    }

    [Fact]
    public void ParseSpot_WithBitgetPair_ReturnsSpotSymbol()
    {
        var result = BitgetSymbolFormatter.ParseSpot("BTCUSDT");

        Assert.Equal(new SpotSymbol("BTC", "USDT"), result);
    }

    [Fact]
    public void ParseSpot_WithNonFiatQuote_ReturnsSpotSymbol()
    {
        var result = BitgetSymbolFormatter.ParseSpot("ETHBTC");

        Assert.Equal(new SpotSymbol("ETH", "BTC"), result);
    }

    [Fact]
    public void ParseSpot_WithLowercaseInput_NormalizesToUppercase()
    {
        var result = BitgetSymbolFormatter.ParseSpot("btcusdt");

        Assert.Equal(new SpotSymbol("BTC", "USDT"), result);
    }

    [Fact]
    public void ParseSpot_WithSurroundingWhitespace_TrimsInput()
    {
        var result = BitgetSymbolFormatter.ParseSpot("  BTCUSDT  ");

        Assert.Equal(new SpotSymbol("BTC", "USDT"), result);
    }

    [Theory]
    [InlineData("ABCUSDT", "ABC", "USDT")] // USDT 先于 USD 匹配
    [InlineData("ABCUSD", "ABC", "USD")]
    [InlineData("ABCFDUSD", "ABC", "FDUSD")] // FDUSD 先于 USD 匹配
    public void ParseSpot_WithAmbiguousSuffix_PrefersLongestMatch(string raw, string expectedBase, string expectedQuote)
    {
        var result = BitgetSymbolFormatter.ParseSpot(raw);

        Assert.Equal(new SpotSymbol(expectedBase, expectedQuote), result);
    }

    [Fact]
    public void Roundtrip_SpotSymbol_PreservesSemantics()
    {
        var symbol = new SpotSymbol("BTC", "USDT");

        var parsed = BitgetSymbolFormatter.ParseSpot(BitgetSymbolFormatter.FormatSpot(symbol));

        Assert.Equal(symbol, parsed);
    }

    [Fact]
    public void Roundtrip_BitgetPair_PreservesRawString()
    {
        var result = BitgetSymbolFormatter.FormatSpot(BitgetSymbolFormatter.ParseSpot("BTCUSDT"));

        Assert.Equal("BTCUSDT", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BTCXXX")] // 无已知计价币后缀
    [InlineData("USDT")] // 后缀即全串，base 为空
    public void ParseSpot_WithInvalidInput_ThrowsArgumentException(string raw)
    {
        Assert.Throws<ArgumentException>(() => BitgetSymbolFormatter.ParseSpot(raw));
    }
}
