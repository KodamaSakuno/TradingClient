using System.Net;
using System.Text;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Exchanges.Gate;

namespace TradingClient.Infrastructure.Tests;

public class GateConnectorInstrumentsTests
{
    private const string CurrencyPairsJson = """
        [
          {
            "id": "BTC_USDT",
            "base": "BTC",
            "quote": "USDT",
            "precision": 2,
            "amount_precision": 4,
            "min_base_amount": "0.0001",
            "min_quote_amount": "1",
            "trade_status": "tradable"
          },
          {
            "id": "ETH_USDT",
            "base": "ETH",
            "quote": "USDT",
            "precision": 3,
            "amount_precision": 2,
            "min_base_amount": "0.01",
            "trade_status": "buyable"
          },
          {
            "id": "DOGE_USDT",
            "base": "DOGE",
            "quote": "USDT",
            "precision": 5,
            "amount_precision": 0,
            "min_base_amount": null,
            "min_quote_amount": null,
            "trade_status": "untradable"
          }
        ]
        """;

    [Fact]
    public async Task GetInstrumentsAsync_WithSpotProduct_MapsInstrumentFields()
    {
        var connector = CreateConnector(CurrencyPairsJson);

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
    public async Task GetInstrumentsAsync_WithNonTradableStatus_MapsToSuspended()
    {
        var connector = CreateConnector(CurrencyPairsJson);

        var instruments = await connector.GetInstrumentsAsync(
            ProductKind.Spot, TestContext.Current.CancellationToken);

        var eth = Assert.Single(instruments, i => i.Symbol.Equals(new SpotSymbol("ETH", "USDT")));
        Assert.Equal(InstrumentStatus.Suspended, eth.Status);
        Assert.Equal(0.001m, eth.TickSize);
        Assert.Equal(0.01m, eth.StepSize);
    }

    [Fact]
    public async Task GetInstrumentsAsync_WithNullMinBaseAmount_MapsToZeroMinQuantity()
    {
        var connector = CreateConnector(CurrencyPairsJson);

        var instruments = await connector.GetInstrumentsAsync(
            ProductKind.Spot, TestContext.Current.CancellationToken);

        var doge = Assert.Single(instruments, i => i.Symbol.Equals(new SpotSymbol("DOGE", "USDT")));
        Assert.Equal(0m, doge.MinQuantity);
        Assert.Null(doge.MinQuoteAmount);
        Assert.Equal(1m, doge.StepSize);
    }

    [Theory]
    [InlineData(ProductKind.Options)]
    public async Task GetInstrumentsAsync_WithUnsupportedProduct_ThrowsNotSupported(ProductKind product)
    {
        var connector = CreateConnector(CurrencyPairsJson);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            connector.GetInstrumentsAsync(product, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInstrumentsAsync_WithHttpError_ThrowsHttpRequestException()
    {
        var connector = new GateConnector(new HttpClient(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            connector.GetInstrumentsAsync(ProductKind.Spot, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Capabilities_Always_DeclareClassicSpotAndFutures()
    {
        var connector = CreateConnector(CurrencyPairsJson);

        Assert.Equal("Gate", connector.ExchangeId);
        Assert.Equal(AccountMode.Classic, connector.Capabilities.AccountMode);
        Assert.True(connector.Capabilities.RequiresInternalTransfers);
        Assert.Equal([ProductKind.Spot, ProductKind.Futures], connector.Capabilities.Products);
    }

    private static GateConnector CreateConnector(string json) =>
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
