using System.Net;
using System.Text;
using TradingClient.Exchanges.Common;
using TradingClient.Exchanges.Gate.Auth;

namespace TradingClient.Infrastructure.Tests;

public class GateAuthHandlerTests
{
    [Fact]
    public async Task SendAsync_Always_AddsKeyTimestampAndSignHeaders()
    {
        var capture = new CapturingHandler();
        var timeSync = new ServerTimeSync();
        var client = CreateClient(capture, timeSync);

        await client.GetAsync(
            "https://api.gateio.ws/api/v4/spot/orders?currency_pair=BTC_USDT&status=open",
            TestContext.Current.CancellationToken);

        var request = capture.LastRequest!;
        Assert.Equal("test-key", request.Headers.GetValues("KEY").Single());
        Assert.True(request.Headers.Contains("Timestamp"));
        Assert.True(request.Headers.Contains("SIGN"));
    }

    [Fact]
    public async Task SendAsync_WithGetRequest_SignsUsingUriPathQueryAndEmptyBody()
    {
        var capture = new CapturingHandler();
        var timeSync = new ServerTimeSync();
        var client = CreateClient(capture, timeSync);

        await client.GetAsync(
            "https://api.gateio.ws/api/v4/spot/orders?currency_pair=BTC_USDT&status=open",
            TestContext.Current.CancellationToken);

        var request = capture.LastRequest!;
        var timestamp = long.Parse(request.Headers.GetValues("Timestamp").Single());
        var expected = GateSigner.Sign(
            "test-secret", "GET", "/api/v4/spot/orders",
            "currency_pair=BTC_USDT&status=open", body: null, timestamp);

        Assert.Equal(expected, request.Headers.GetValues("SIGN").Single());
    }

    [Fact]
    public async Task SendAsync_WithPostBody_SignsRawBodyContent()
    {
        var capture = new CapturingHandler();
        var timeSync = new ServerTimeSync();
        var client = CreateClient(capture, timeSync);
        const string body = """{"currency_pair":"BTC_USDT","side":"buy","amount":"0.001","price":"30000"}""";

        await client.PostAsync(
            "https://api.gateio.ws/api/v4/spot/orders",
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        var request = capture.LastRequest!;
        var timestamp = long.Parse(request.Headers.GetValues("Timestamp").Single());
        var expected = GateSigner.Sign(
            "test-secret", "POST", "/api/v4/spot/orders",
            query: null, body, timestamp);

        Assert.Equal(expected, request.Headers.GetValues("SIGN").Single());
    }

    [Fact]
    public async Task SendAsync_AfterTimeSyncUpdate_UsesServerCalibratedTimestamp()
    {
        var capture = new CapturingHandler();
        var timeSync = new ServerTimeSync();
        // 服务器比本地快 42 秒
        var serverTime = DateTimeOffset.UtcNow.AddSeconds(42);
        timeSync.Update(serverTime);
        var client = CreateClient(capture, timeSync);

        await client.GetAsync("https://api.gateio.ws/api/v4/spot/accounts", TestContext.Current.CancellationToken);

        var timestamp = long.Parse(capture.LastRequest!.Headers.GetValues("Timestamp").Single());
        Assert.InRange(Math.Abs(timestamp - serverTime.ToUnixTimeSeconds()), 0, 2);
    }

    private static HttpClient CreateClient(HttpMessageHandler inner, ServerTimeSync timeSync) =>
        new(new GateAuthHandler(new GateCredentials("test-key", "test-secret"), timeSync)
        {
            InnerHandler = inner,
        });

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
