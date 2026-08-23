using System.Net;
using System.Text;
using TradingClient.Application.Abstractions;
using TradingClient.Exchanges.ContractTests.Contract;
using TradingClient.Exchanges.Gate;

namespace TradingClient.Exchanges.ContractTests;

public class GateClassicMarketDataTests : MarketDataContractTests
{
    private static readonly string CurrencyPairsJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate_spot_currency_pairs.json"));

    protected override IMarketData CreateConnector() =>
        new GateConnector(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CurrencyPairsJson, Encoding.UTF8, "application/json"),
        })));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
