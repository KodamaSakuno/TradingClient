using System.Net;
using System.Text;
using TradingClient.Exchanges.Bitget.Auth;
using TradingClient.Exchanges.Common;

namespace TradingClient.Infrastructure.Tests;

public class BitgetAuthHandlerTests
{
    [Fact]
    public async Task SendAsync_Always_AddsFourAuthHeaders()
    {
        var capture = new CapturingHandler();
        var client = CreateClient(capture, new ServerTimeSync());

        await client.GetAsync(
            "https://api.bitget.com/api/v3/account/assets?coin=USDT",
            TestContext.Current.CancellationToken);

        var request = capture.LastRequest!;
        Assert.Equal("test-key", request.Headers.GetValues("ACCESS-KEY").Single());
        Assert.Equal("test-passphrase", request.Headers.GetValues("ACCESS-PASSPHRASE").Single());
        Assert.True(request.Headers.Contains("ACCESS-TIMESTAMP"));
        Assert.True(request.Headers.Contains("ACCESS-SIGN"));
    }

    [Fact]
    public async Task SendAsync_WithGetRequest_SignMatchesBitgetSignerOutput()
    {
        var capture = new CapturingHandler();
        var client = CreateClient(capture, new ServerTimeSync());

        await client.GetAsync(
            "https://api.bitget.com/api/v3/account/assets?coin=USDT",
            TestContext.Current.CancellationToken);

        var request = capture.LastRequest!;
        var timestamp = request.Headers.GetValues("ACCESS-TIMESTAMP").Single();
        var expected = BitgetSigner.Sign(
            "test-secret", timestamp, "GET", "/api/v3/account/assets",
            "coin=USDT", body: null);

        Assert.Equal(expected, request.Headers.GetValues("ACCESS-SIGN").Single());
    }

    [Fact]
    public async Task SendAsync_WithPostBody_SignsRawBodyContent()
    {
        var capture = new CapturingHandler();
        var client = CreateClient(capture, new ServerTimeSync());
        const string body = """{"symbol":"BTCUSDT","side":"buy"}""";

        await client.PostAsync(
            "https://api.bitget.com/api/v3/trade/place-order",
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        var request = capture.LastRequest!;
        var timestamp = request.Headers.GetValues("ACCESS-TIMESTAMP").Single();
        var expected = BitgetSigner.Sign(
            "test-secret", timestamp, "POST", "/api/v3/trade/place-order",
            query: null, body);

        Assert.Equal(expected, request.Headers.GetValues("ACCESS-SIGN").Single());
    }

    [Fact]
    public async Task SendAsync_AfterTimeSyncUpdate_UsesServerCalibratedMillisecondTimestamp()
    {
        var capture = new CapturingHandler();
        var timeSync = new ServerTimeSync();
        // 服务器比本地快 42 秒
        var serverTime = DateTimeOffset.UtcNow.AddSeconds(42);
        timeSync.Update(serverTime);
        var client = CreateClient(capture, timeSync);

        await client.GetAsync("https://api.bitget.com/api/v3/account/assets", TestContext.Current.CancellationToken);

        var timestamp = long.Parse(capture.LastRequest!.Headers.GetValues("ACCESS-TIMESTAMP").Single());
        Assert.InRange(Math.Abs(timestamp - serverTime.ToUnixTimeMilliseconds()), 0, 2000);
    }

    [Fact]
    public async Task SendAsync_WithDemoTrading_AddsPaptradingHeader()
    {
        var capture = new CapturingHandler();
        var client = CreateClient(capture, new ServerTimeSync(), demoTrading: true);

        await client.GetAsync("https://api.bitget.com/api/v3/account/assets", TestContext.Current.CancellationToken);

        Assert.Equal("1", capture.LastRequest!.Headers.GetValues("paptrading").Single());
    }

    [Fact]
    public async Task SendAsync_WithoutDemoTrading_OmitsPaptradingHeader()
    {
        var capture = new CapturingHandler();
        var client = CreateClient(capture, new ServerTimeSync(), demoTrading: false);

        await client.GetAsync("https://api.bitget.com/api/v3/account/assets", TestContext.Current.CancellationToken);

        Assert.False(capture.LastRequest!.Headers.Contains("paptrading"));
    }

    private static HttpClient CreateClient(HttpMessageHandler inner, ServerTimeSync timeSync, bool demoTrading = false) =>
        new(new BitgetAuthHandler(new BitgetCredentials("test-key", "test-secret", "test-passphrase"), timeSync, demoTrading)
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
