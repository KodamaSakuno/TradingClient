using System.Net;
using System.Text;
using TradingClient.Domain.Primitives;
using TradingClient.Exchanges.Bitget;
using TradingClient.Exchanges.Bitget.Auth;

namespace TradingClient.Infrastructure.Tests;

public class BitgetConnectorAccountTests
{
    // 官方文档示例响应，出处：.local/bitget/catalog/uta-account-assets-balance/assets-balance-assets.md，核对日期 2026-08-26
    private const string AccountAssetsJson = """
        {
          "code": "00000",
          "msg": "success",
          "requestTime": 1746687063471,
          "data": {
            "accountEquity": "11.13919278",
            "usdtEquity": "11.13921165",
            "btcEquity": "0.00011256",
            "unrealisedPnl": "0",
            "usdtUnrealisedPnl": "0",
            "btcUnrealizedPnl": "0",
            "effEquity": "6.19299777",
            "mmr": "0",
            "imr": "0",
            "mgnRatio": "0",
            "positionMgnRatio": "0",
            "positionValue": "0",
            "leverage": "1",
            "assets": [
              {
                "coin": "USDT",
                "equity": "6.19300826",
                "usdValue": "6.19299777",
                "balance": "6.19300826",
                "available": "6.19300826",
                "debt": "0",
                "locked": "0",
                "bonus": "10"
              },
              {
                "coin": "BGB",
                "equity": "1.15582129",
                "usdValue": "4.94618029",
                "balance": "1.15582129",
                "available": "1.15582129",
                "debt": "0",
                "locked": "0",
                "bonus": "0"
              }
            ]
          }
        }
        """;

    [Fact]
    public async Task GetAccountAsync_WithOfficialExampleJson_MapsAccountSummaryFields()
    {
        var connector = CreateConnector(OkJson(AccountAssetsJson), out _);

        var result = await connector.GetAccountAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var summary = result.Value!;
        Assert.Equal(AccountMode.Unified, summary.Mode);
        Assert.Equal(11.13919278m, summary.TotalEquity);
        Assert.Equal(0m, summary.InitialMargin);
        Assert.Equal(0m, summary.MaintenanceMargin);
        Assert.Equal(0m, summary.MarginRatio);
        // 推导口径：effEquity − imr
        Assert.Equal(6.19299777m, summary.AvailableMargin);

        Assert.Equal(2, summary.Assets.Count);

        var usdt = Assert.Single(summary.Assets, a => a.Asset == "USDT");
        Assert.Equal(6.19300826m, usdt.Total);
        Assert.Equal(0m, usdt.Frozen);
        Assert.Null(usdt.CollateralWeight);
        Assert.Equal(6.19299777m, usdt.EquityValue);

        var bgb = Assert.Single(summary.Assets, a => a.Asset == "BGB");
        Assert.Equal(1.15582129m, bgb.Total);
        Assert.Equal(4.94618029m, bgb.EquityValue);
    }

    [Fact]
    public async Task GetAccountAsync_WithoutCredentials_ReturnsMissingCredentialsFailure()
    {
        var connector = new BitgetConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("{}"))));

        var result = await connector.GetAccountAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("MISSING_CREDENTIALS", result.Error!.Code);
    }

    [Fact]
    public async Task GetAccountAsync_WithHttp400_ReturnsErrorCodeFromBody()
    {
        const string errorJson = """{"code":"25202","msg":"余额不足"}""";
        var connector = CreateConnector(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(errorJson, Encoding.UTF8, "application/json"),
            }, out _);

        var result = await connector.GetAccountAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("25202", result.Error!.Code);
        Assert.Equal("余额不足", result.Error.Message);
    }

    [Fact]
    public async Task GetAccountAsync_SendsSignedRequestHeaders()
    {
        var connector = CreateConnector(OkJson(AccountAssetsJson), out var captured);

        var result = await connector.GetAccountAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(captured.Requests);
        Assert.Equal("/api/v3/account/assets", request.RequestUri!.AbsolutePath);
        Assert.Equal("test-key", request.Headers.GetValues("ACCESS-KEY").Single());
        Assert.True(long.TryParse(request.Headers.GetValues("ACCESS-TIMESTAMP").Single(), out _));
        Assert.True(request.Headers.Contains("ACCESS-SIGN"));
        Assert.Equal("test-passphrase", request.Headers.GetValues("ACCESS-PASSPHRASE").Single());
    }

    private static BitgetConnector CreateConnector(HttpResponseMessage response, out CapturingHandler captured)
    {
        var handler = new CapturingHandler(response);
        captured = handler;
        return new BitgetConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("{}"))),
            BitgetConnector.DefaultBaseUrl,
            credentials: new BitgetCredentials("test-key", "test-secret", "test-passphrase"),
            demoTrading: false,
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
