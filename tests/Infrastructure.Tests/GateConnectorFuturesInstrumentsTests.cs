using System.Net;
using System.Text;
using TradingClient.Domain.Instruments;
using TradingClient.Exchanges.Gate;

namespace TradingClient.Infrastructure.Tests;

public class GateConnectorFuturesInstrumentsTests
{
    // 内嵌 JSON 仿 testnet fixture 结构（tests/Exchanges.ContractTests/Fixtures/gate_futures_usdt_contracts.json，2026-08-27 录制）
    // order_size_min 为裸数字形态（testnet 实测）
    private const string ContractsJsonNumericSize = """
        [
          {
            "name": "BTC_USDT",
            "quanto_multiplier": "0.0001",
            "order_price_round": "0.1",
            "order_size_min": 1,
            "order_size_max": 1000000,
            "enable_decimal": false,
            "status": "trading",
            "in_delisting": false
          },
          {
            "name": "ETH_USDT",
            "quanto_multiplier": "0.01",
            "order_price_round": "0.01",
            "order_size_min": 1,
            "order_size_max": 500000,
            "enable_decimal": true,
            "status": "prelaunch",
            "in_delisting": false
          },
          {
            "name": "DOGE_USDT",
            "quanto_multiplier": "10",
            "order_price_round": "0.00001",
            "order_size_min": 1,
            "order_size_max": 1000000,
            "enable_decimal": false,
            "status": "trading",
            "in_delisting": true
          }
        ]
        """;

    // order_size_min 为字符串形态（文档示例）
    private const string ContractsJsonStringSize = """
        [
          {
            "name": "BTC_USDT",
            "quanto_multiplier": "0.0001",
            "order_price_round": "0.1",
            "order_size_min": "1",
            "order_size_max": "1000000",
            "enable_decimal": false,
            "status": "trading",
            "in_delisting": false
          }
        ]
        """;

    [Fact]
    public async Task GetInstrumentsAsync_WithFuturesProduct_MapsInstrumentFields()
    {
        var connector = CreateConnector(ContractsJsonNumericSize);

        var instruments = await connector.GetInstrumentsAsync(
            ProductKind.Futures, TestContext.Current.CancellationToken);

        var btc = Assert.Single(instruments, i => i.Symbol.Equals(new PerpetualFuturesSymbol("BTC", "USDT")));
        Assert.Equal(ProductKind.Futures, btc.Product);
        // 张 → 币换算：1 张 × 0.0001 = 0.0001 币
        Assert.Equal(0.0001m, btc.MinQuantity);
        Assert.Equal(0.0001m, btc.StepSize);
        // order_price_round 是显式 tick，直解析而非 Pow10Negative
        Assert.Equal(0.1m, btc.TickSize);
        Assert.Equal(0.0001m, btc.ContractMultiplier);
        Assert.Null(btc.MinQuoteAmount);
        Assert.Equal(InstrumentStatus.Trading, btc.Status);
    }

    [Fact]
    public async Task GetInstrumentsAsync_WithNonTradingStatus_MapsToSuspended()
    {
        var connector = CreateConnector(ContractsJsonNumericSize);

        var instruments = await connector.GetInstrumentsAsync(
            ProductKind.Futures, TestContext.Current.CancellationToken);

        var eth = Assert.Single(instruments, i => i.Symbol.Equals(new PerpetualFuturesSymbol("ETH", "USDT")));
        Assert.Equal(InstrumentStatus.Suspended, eth.Status);
    }

    [Fact]
    public async Task GetInstrumentsAsync_WithInDelistingTrue_MapsToSuspended()
    {
        var connector = CreateConnector(ContractsJsonNumericSize);

        var instruments = await connector.GetInstrumentsAsync(
            ProductKind.Futures, TestContext.Current.CancellationToken);

        // status=trading 但 in_delisting=true，仍归 Suspended
        var doge = Assert.Single(instruments, i => i.Symbol.Equals(new PerpetualFuturesSymbol("DOGE", "USDT")));
        Assert.Equal(InstrumentStatus.Suspended, doge.Status);
    }

    [Fact]
    public async Task GetInstrumentsAsync_WithStringOrderSizeMin_ParsesSameAsNumber()
    {
        var connector = CreateConnector(ContractsJsonStringSize);

        var instruments = await connector.GetInstrumentsAsync(
            ProductKind.Futures, TestContext.Current.CancellationToken);

        var btc = Assert.Single(instruments);
        Assert.Equal(0.0001m, btc.MinQuantity);
        Assert.Equal(0.0001m, btc.StepSize);
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
