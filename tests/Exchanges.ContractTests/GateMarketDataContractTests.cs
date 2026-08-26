using System.Net;
using System.Text;
using TradingClient.Application.Abstractions;
using TradingClient.Exchanges.ContractTests.Contract;
using TradingClient.Exchanges.Gate;

namespace TradingClient.Exchanges.ContractTests;

public class GateClassicMarketDataTests : MarketDataContractTests
{
    // 均录自 Gate testnet（2026-08-27）：GET /spot/currency_pairs 与 GET /futures/usdt/contracts
    private static readonly string CurrencyPairsJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate_spot_currency_pairs.json"));

    private static readonly string FuturesContractsJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate_futures_usdt_contracts.json"));

    protected override IMarketData CreateConnector() =>
        new GateConnector(new HttpClient(new StubHttpMessageHandler(request =>
        {
            // 按产品线路由：/futures/ 前缀返回合约 fixture，其余（/spot/）返回现货 fixture
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var json = path.Contains("/futures/", StringComparison.Ordinal)
                ? FuturesContractsJson
                : CurrencyPairsJson;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        })));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
