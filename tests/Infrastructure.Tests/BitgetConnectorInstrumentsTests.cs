using System.Net;
using System.Text;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Exchanges.Bitget;

namespace TradingClient.Infrastructure.Tests;

public class BitgetConnectorInstrumentsTests
{
    private const string InstrumentsJson = """
        {
          "code": "00000",
          "msg": "success",
          "requestTime": 1787773045144,
          "data": [
            {
              "symbol": "BTCUSDT",
              "category": "SPOT",
              "baseCoin": "BTC",
              "quoteCoin": "USDT",
              "minOrderQty": "0.0001",
              "pricePrecision": "2",
              "quantityPrecision": "4",
              "minOrderAmount": "1",
              "status": "online"
            },
            {
              "symbol": "ETHUSDT",
              "category": "SPOT",
              "baseCoin": "ETH",
              "quoteCoin": "USDT",
              "minOrderQty": "0.01",
              "pricePrecision": "3",
              "quantityPrecision": "2",
              "minOrderAmount": "1",
              "status": "offline"
            },
            {
              "symbol": "RPBRUSDT",
              "category": "SPOT",
              "baseCoin": "rPBR",
              "quoteCoin": "USDT",
              "minOrderQty": "0.0001",
              "pricePrecision": "2",
              "quantityPrecision": "0",
              "minOrderAmount": "",
              "status": "online"
            }
          ]
        }
        """;

    [Fact]
    public async Task GetInstrumentsAsync_WithSpotProduct_MapsInstrumentFields()
    {
        var connector = CreateConnector(InstrumentsJson);

        var instruments = await connector.GetInstrumentsAsync(
            ProductKind.Spot, TestContext.Current.CancellationToken);

        var btc = Assert.Single(instruments, i => i.Symbol.Equals(new SpotSymbol("BTC", "USDT")));
        Assert.Equal(new SpotSymbol("BTC", "USDT"), btc.Symbol);
        Assert.Equal(0.01m, btc.TickSize);
        Assert.Equal(0.0001m, btc.StepSize);
        Assert.Equal(0.0001m, btc.MinQuantity);
        Assert.Equal(1m, btc.MinQuoteAmount);
        Assert.Null(btc.ContractMultiplier);
        Assert.Equal(InstrumentStatus.Trading, btc.Status);
    }

    [Fact]
    public async Task GetInstrumentsAsync_WithNonOnlineStatus_MapsToSuspended()
    {
        var connector = CreateConnector(InstrumentsJson);

        var instruments = await connector.GetInstrumentsAsync(
            ProductKind.Spot, TestContext.Current.CancellationToken);

        var eth = Assert.Single(instruments, i => i.Symbol.Equals(new SpotSymbol("ETH", "USDT")));
        Assert.Equal(InstrumentStatus.Suspended, eth.Status);
        Assert.Equal(0.001m, eth.TickSize);
        Assert.Equal(0.01m, eth.StepSize);
    }

    [Fact]
    public async Task GetInstrumentsAsync_WithMixedCaseBaseCoin_UppercasesSymbolAndNullsEmptyMinOrderAmount()
    {
        var connector = CreateConnector(InstrumentsJson);

        var instruments = await connector.GetInstrumentsAsync(
            ProductKind.Spot, TestContext.Current.CancellationToken);

        var rpbr = Assert.Single(instruments, i => i.Symbol.Equals(new SpotSymbol("RPBR", "USDT")));
        Assert.Null(rpbr.MinQuoteAmount);
        Assert.Equal(1m, rpbr.StepSize);
    }

    [Theory]
    [InlineData(ProductKind.Futures)]
    [InlineData(ProductKind.Options)]
    public async Task GetInstrumentsAsync_WithUnsupportedProduct_ThrowsNotSupported(ProductKind product)
    {
        var connector = CreateConnector(InstrumentsJson);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            connector.GetInstrumentsAsync(product, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInstrumentsAsync_WithHttpError_ThrowsHttpRequestException()
    {
        var connector = new BitgetConnector(new HttpClient(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            connector.GetInstrumentsAsync(ProductKind.Spot, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Capabilities_Always_DeclareUnifiedSpotOnly()
    {
        var connector = CreateConnector(InstrumentsJson);

        Assert.Equal("Bitget", connector.ExchangeId);
        Assert.Equal(AccountMode.Unified, connector.Capabilities.AccountMode);
        Assert.False(connector.Capabilities.RequiresInternalTransfers);
        Assert.Equal([ProductKind.Spot], connector.Capabilities.Products);
    }

    private static BitgetConnector CreateConnector(string json) =>
        new(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        })));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
