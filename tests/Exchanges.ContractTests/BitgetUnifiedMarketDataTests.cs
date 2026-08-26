using System.Net;
using System.Text;
using TradingClient.Application.Abstractions;
using TradingClient.Exchanges.Bitget;
using TradingClient.Exchanges.ContractTests.Contract;

namespace TradingClient.Exchanges.ContractTests;

public class BitgetUnifiedMarketDataTests : MarketDataContractTests
{
    // fixture 出处：2026-08-26 录自生产环境 GET /api/v3/market/instruments?category=SPOT
    private static readonly string InstrumentsJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bitget_v3_spot_instruments.json"));

    protected override IMarketData CreateConnector() =>
        new BitgetConnector(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(InstrumentsJson, Encoding.UTF8, "application/json"),
        })));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
