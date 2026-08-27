using System.Net;
using System.Text;
using System.Text.Json;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;

namespace TradingClient.Infrastructure.Tests;

public class GateConnectorDeadManSwitchTests
{
    private static GateConnector CreateConnector(CapturingHandler captured, TimeSpan? deadManInterval) =>
        new(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("[]"))),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException(), // 本组测试不用 WS
            credentials: new GateCredentials("test-key", "test-secret"),
            authInnerHandler: captured,
            futuresDeadManInterval: deadManInterval);

    private static List<string> CountdownBodies(CapturingHandler captured) =>
        captured.Requests
            .Where(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/futures/usdt/countdown_cancel_all", StringComparison.Ordinal))
            .Select(r => r.Body!)
            .ToList();

    private static int TimeoutOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("timeout").GetInt32();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task Constructor_WithDeadManInterval_RenewsCountdownWithTripledTimeout()
    {
        var captured = new CapturingHandler();
        await using var connector = CreateConnector(captured, TimeSpan.FromMilliseconds(150));

        // timeout = interval × 3，下限 5 秒（文档约束）：150ms × 3 = 0.45s → 5
        await WaitUntilAsync(() => CountdownBodies(captured).Count >= 2);

        Assert.All(CountdownBodies(captured), body => Assert.Equal(5, TimeoutOf(body)));
    }

    [Fact]
    public async Task DisposeAsync_AfterRenewals_SendsTimeoutZeroToDisable()
    {
        var captured = new CapturingHandler();
        var connector = CreateConnector(captured, TimeSpan.FromMilliseconds(150));
        await WaitUntilAsync(() => CountdownBodies(captured).Count >= 1);

        await connector.DisposeAsync();

        // 最后一次 countdown 调用是 timeout=0：正常退出主动关闭倒计时，避免误撤单
        Assert.Equal(0, TimeoutOf(CountdownBodies(captured).Last()));
    }

    [Fact]
    public async Task Constructor_WithoutDeadManInterval_SendsNoCountdown()
    {
        var captured = new CapturingHandler();
        await using var connector = CreateConnector(captured, deadManInterval: null);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Empty(CountdownBodies(captured));
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

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<(HttpRequestMessage Request, string? Body)> _requests = [];

        public IReadOnlyList<(HttpRequestMessage Request, string? Body)> Requests
        {
            get { lock (_gate) return _requests.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
                _requests.Add((request, body));
            return OkJson("{}");
        }
    }
}
