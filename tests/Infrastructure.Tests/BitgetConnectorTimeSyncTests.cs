using System.Net;
using System.Text;
using TradingClient.Exchanges.Bitget;

namespace TradingClient.Infrastructure.Tests;

public class BitgetConnectorTimeSyncTests
{
    // V3 无公共时间接口，校时走 V2 /api/v2/public/time（跨版本怪癖）
    [Fact]
    public async Task ConnectAsync_WhenV2PublicTimeSucceeds_CalibratesServerTimeOffset()
    {
        var serverTime = DateTimeOffset.UtcNow.AddSeconds(42);
        var ms = serverTime.ToUnixTimeMilliseconds();
        var json = "{\"code\":\"00000\",\"msg\":\"success\",\"requestTime\":" + ms
            + ",\"data\":{\"serverTime\":\"" + ms + "\"}}";
        var connector = new BitgetConnector(new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            })));

        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(serverTime, connector.TimeSync.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ConnectAsync_WhenV2PublicTimeFails_StillConnectsWithLocalClock()
    {
        var connector = new BitgetConnector(new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DateTimeOffset.UtcNow, connector.TimeSync.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void CreateAuthenticatedHttpClient_WithoutCredentials_Throws()
    {
        var connector = new BitgetConnector(new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK))));

        Assert.Throws<InvalidOperationException>(() => connector.CreateAuthenticatedHttpClient());
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
