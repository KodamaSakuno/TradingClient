using System.Net;
using System.Text;
using TradingClient.Domain.Primitives;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;

namespace TradingClient.Infrastructure.Tests;

public class GateConnectorAccountTests
{
    // 形态对齐 .local/gate_api_spot_restful.txt:1104 的录制响应，补充 locked 非 0 与多币种
    private const string AccountsJson = """
        [
          { "currency": "USDT", "available": "968.8", "locked": "0", "update_id": 98 },
          { "currency": "BTC", "available": "0.5", "locked": "0.25", "update_id": 12 },
          { "currency": "ETH", "available": "0", "locked": "3.14", "update_id": 7 }
        ]
        """;

    [Fact]
    public async Task GetAccountAsync_WithRecordedJson_MapsAccountSummaryFields()
    {
        var connector = CreateConnector(OkJson(AccountsJson), out _);

        var result = await connector.GetAccountAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var summary = result.Value!;
        Assert.Equal(AccountMode.Classic, summary.Mode);
        Assert.Equal(0m, summary.TotalEquity);
        Assert.Equal(0m, summary.AvailableMargin);
        Assert.Equal(0m, summary.InitialMargin);
        Assert.Equal(0m, summary.MaintenanceMargin);
        Assert.Equal(0m, summary.MarginRatio);

        Assert.Equal(3, summary.Assets.Count);

        var usdt = Assert.Single(summary.Assets, a => a.Asset == "USDT");
        Assert.Equal(968.8m, usdt.Total);
        Assert.Equal(0m, usdt.Frozen);
        Assert.Null(usdt.CollateralWeight);
        Assert.Equal(968.8m, usdt.EquityValue);

        var btc = Assert.Single(summary.Assets, a => a.Asset == "BTC");
        Assert.Equal(0.75m, btc.Total);
        Assert.Equal(0.25m, btc.Frozen);

        var eth = Assert.Single(summary.Assets, a => a.Asset == "ETH");
        Assert.Equal(3.14m, eth.Total);
        Assert.Equal(3.14m, eth.Frozen);
    }

    [Fact]
    public async Task GetAccountAsync_WithoutCredentials_ReturnsMissingCredentialsFailure()
    {
        var connector = new GateConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("[]"))),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException());

        var result = await connector.GetAccountAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("MISSING_CREDENTIALS", result.Error!.Code);
    }

    [Fact]
    public async Task GetAccountAsync_WithHttp401_ReturnsLabelCodedFailure()
    {
        const string errorJson = """{"label":"INVALID_KEY","message":"Invalid key provided"}""";
        var connector = CreateConnector(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(errorJson, Encoding.UTF8, "application/json"),
            }, out _);

        var result = await connector.GetAccountAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_KEY", result.Error!.Code);
        Assert.Equal("Invalid key provided", result.Error.Message);
    }

    [Fact]
    public async Task GetAccountAsync_SendsSignedRequestHeaders()
    {
        var connector = CreateConnector(OkJson(AccountsJson), out var captured);

        var result = await connector.GetAccountAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(captured.Requests);
        Assert.Equal("/api/v4/spot/accounts", request.RequestUri!.AbsolutePath);
        Assert.Equal("test-key", request.Headers.GetValues("KEY").Single());
        Assert.True(long.TryParse(request.Headers.GetValues("Timestamp").Single(), out _));
        Assert.Matches("^[0-9a-f]{128}$", request.Headers.GetValues("SIGN").Single());
    }

    private static GateConnector CreateConnector(HttpResponseMessage response, out CapturingHandler captured)
    {
        var handler = new CapturingHandler(response);
        captured = handler;
        return new GateConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("[]"))),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException(),
            credentials: new GateCredentials("test-key", "test-secret"),
            authInnerHandler: handler);
    }

    private static HttpResponseMessage OkJson(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }
}
