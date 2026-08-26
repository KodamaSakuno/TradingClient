using System.Net;
using System.Text;
using TradingClient.Exchanges.Gate;

namespace TradingClient.Infrastructure.Tests;

public class GateConnectorTimeSyncTests
{
    [Fact]
    public async Task ConnectAsync_WhenSpotTimeSucceeds_CalibratesServerTimeOffset()
    {
        var serverTime = DateTimeOffset.UtcNow.AddSeconds(42);
        var json = $$"""{"server_time": {{serverTime.ToUnixTimeMilliseconds()}}}""";
        var connector = new GateConnector(new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            })));

        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(serverTime, connector.TimeSync.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ConnectAsync_WhenSpotTimeFails_StillConnectsWithLocalClock()
    {
        var connector = new GateConnector(new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        await connector.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DateTimeOffset.UtcNow, connector.TimeSync.UtcNow, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void CreateAuthenticatedHttpClient_WithoutCredentials_Throws()
    {
        var connector = new GateConnector(new HttpClient(new StubHttpMessageHandler(_ =>
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
