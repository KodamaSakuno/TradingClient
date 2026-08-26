using System.Net;
using System.Text;
using TradingClient.Application.Abstractions;
using TradingClient.Exchanges.ContractTests.Contract;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;

namespace TradingClient.Exchanges.ContractTests;

public class GateClassicAccountServiceTests : AccountServiceContractTests
{
    private static readonly string AccountsJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate_spot_accounts.json"));

    protected override IAccountService CreateConnector() =>
        new GateConnector(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException(),
            credentials: new GateCredentials("test-key", "test-secret"),
            authInnerHandler: new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(AccountsJson, Encoding.UTF8, "application/json"),
            }));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
