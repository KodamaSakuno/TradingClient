using System.Text.Json;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Bitget.Auth;
using TradingClient.Exchanges.Bitget.WebSocket;
using TradingClient.Exchanges.Common;

namespace TradingClient.Infrastructure.Tests;

public class BitgetPrivateWsClientTests
{
    private static readonly BitgetCredentials Credentials = new("test-key", "test-secret", "test-passphrase");

    private const string LoginSuccessJson = """{"event":"login","code":"0","msg":""}""";
    private const string LoginFailJson = """{"event":"error","code":"30005","msg":"login fail"}""";

    // 推送帧样本形态对齐官方文档示例（.local/bitget/uta/websocket/private/Order-Channel.md），
    // category 换成现货、字段精简到映射所需
    private static string OrderPushJson(string orderStatus) => $$"""
        {
          "action": "snapshot",
          "arg": { "instType": "UTA", "topic": "order" },
          "data": [
            {
              "category": "spot",
              "symbol": "BTCUSDT",
              "orderId": "1234567890",
              "clientOid": "cid-1",
              "price": "50000",
              "qty": "0.01",
              "side": "buy",
              "orderType": "limit",
              "timeInForce": "gtc",
              "cumExecQty": "0.004",
              "avgPrice": "49999.5",
              "orderStatus": "{{orderStatus}}",
              "createdTime": "1742367838101",
              "updatedTime": "1742367838115"
            }
          ],
          "ts": 1742367838124
        }
        """;

    [Fact]
    public async Task SubscribeSpotOrderUpdates_WithCredentials_SendsLoginBeforeSubscribe()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport, Credentials);
        var collector = new Collector<SpotOrderUpdate>();

        using var sub = client.SubscribeSpotOrderUpdates().Subscribe(collector);

        await WaitForAsync(() => transport.SentFrames.Count == 1, "login frame");
        var frame = ParseFrame(transport.SentFrames[0]);
        Assert.Equal("login", frame.Op);
        var arg = frame.Arg;
        Assert.Equal("test-key", arg.GetProperty("apiKey").GetString());
        Assert.Equal("test-passphrase", arg.GetProperty("passphrase").GetString());

        // WS 登录时间戳是秒级（与 REST 的毫秒不同）
        var timestamp = arg.GetProperty("timestamp").GetString()!;
        var seconds = long.Parse(timestamp);
        Assert.InRange(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds), 0, 60);
        // 签名串固定为 timestamp + "GET" + "/user/verify"
        var expectedSign = BitgetSigner.Sign("test-secret", timestamp, "GET", "/user/verify", null, null);
        Assert.Equal(expectedSign, arg.GetProperty("sign").GetString());

        // login 成功 ack 前不得发订阅
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Single(transport.SentFrames);

        transport.Push(LoginSuccessJson);

        await WaitForAsync(() => transport.SentFrames.Count == 2, "subscribe frame after login");
        var subscribe = ParseFrame(transport.SentFrames[1]);
        Assert.Equal("subscribe", subscribe.Op);
        Assert.Equal("UTA", subscribe.Arg.GetProperty("instType").GetString());
        Assert.Equal("order", subscribe.Arg.GetProperty("topic").GetString());
        Assert.False(subscribe.Arg.TryGetProperty("symbol", out _));
    }

    [Fact]
    public async Task SubscribeSpotOrderUpdates_WithoutCredentials_EmitsOnError()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport, credentials: null);
        var collector = new Collector<SpotOrderUpdate>();

        using var sub = client.SubscribeSpotOrderUpdates().Subscribe(collector);

        await WaitForAsync(() => collector.Errors.Count == 1, "missing credentials error");
        Assert.Contains("credentials", collector.Errors[0].Message);
        Assert.Empty(transport.SentFrames);
    }

    [Fact]
    public async Task LoginError_NotifiesSubscriberAndReconnects()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport, Credentials);
        var collector = new Collector<SpotOrderUpdate>();

        using var sub = client.SubscribeSpotOrderUpdates().Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "login frame");

        transport.Push(LoginFailJson);

        await WaitForAsync(() => collector.Errors.Count == 1, "login fail notification");
        Assert.Contains("30005", collector.Errors[0].Message);

        // login 失败触发重连，重连后重发 login（中间可能夹带 OnError 退订触发的 unsubscribe 帧，按 op 过滤断言）
        await WaitForAsync(() => transport.ConnectCount == 2, "reconnect after login fail");
        await WaitForAsync(
            () => transport.SentFrames.Count(f => ParseFrame(f).Op == "login") == 2,
            "second login frame");
    }

    [Theory]
    [InlineData("new", OrderStatus.New)]
    [InlineData("partially_filled", OrderStatus.PartiallyFilled)]
    [InlineData("filled", OrderStatus.Filled)]
    [InlineData("cancelled", OrderStatus.Cancelled)]
    public async Task OrderPush_AfterLoginAndSubscribe_DispatchesMappedUpdate(string orderStatus, OrderStatus expected)
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport, Credentials);
        var collector = new Collector<SpotOrderUpdate>();

        using var sub = client.SubscribeSpotOrderUpdates().Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "login frame");
        transport.Push(LoginSuccessJson);
        await WaitForAsync(() => transport.SentFrames.Count == 2, "subscribe frame");

        transport.Push(OrderPushJson(orderStatus));

        await WaitForAsync(() => collector.Items.Count == 1, "order update");
        var update = collector.Items[0];
        Assert.Equal("1234567890", update.Order.OrderId);
        Assert.Equal(new SpotSymbol("BTC", "USDT"), update.Order.Symbol);
        Assert.Equal(OrderSide.Buy, update.Order.Side);
        Assert.Equal(OrderType.Limit, update.Order.Type);
        Assert.Equal(50_000m, update.Order.Price);
        Assert.Equal(0.01m, update.Order.Quantity);
        Assert.Equal(0.004m, update.Order.FilledQuantity);
        Assert.Equal(expected, update.Order.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1742367838101), update.Order.CreatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1742367838115), update.Timestamp);
    }

    [Fact]
    public async Task OrderPush_WithNonSpotCategory_IsFiltered()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport, Credentials);
        var collector = new Collector<SpotOrderUpdate>();

        using var sub = client.SubscribeSpotOrderUpdates().Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "login frame");
        transport.Push(LoginSuccessJson);
        await WaitForAsync(() => transport.SentFrames.Count == 2, "subscribe frame");

        // UTA order 频道全产品线同频道推送，现货流须过滤掉合约数据项
        transport.Push(OrderPushJson("filled").Replace("\"category\": \"spot\"", "\"category\": \"usdt-futures\""));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(collector.Items);
        Assert.Empty(collector.Errors);
    }

    [Fact]
    public async Task Reconnect_AfterServerClose_LoginsAgainThenResubscribes()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport, Credentials);
        var collector = new Collector<SpotOrderUpdate>();

        using var sub = client.SubscribeSpotOrderUpdates().Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "login frame");
        transport.Push(LoginSuccessJson);
        await WaitForAsync(() => transport.SentFrames.Count == 2, "subscribe frame");

        transport.Push(null); // 服务端关闭连接

        // 重连后先重发 login，不得直接补订阅
        await WaitForAsync(() => transport.ConnectCount == 2, "reconnect");
        await WaitForAsync(() => transport.SentFrames.Count == 3, "second login frame");
        Assert.Equal("login", ParseFrame(transport.SentFrames[2]).Op);

        transport.Push(LoginSuccessJson);

        await WaitForAsync(() => transport.SentFrames.Count == 4, "resubscribe frame");
        var resubscribe = ParseFrame(transport.SentFrames[3]);
        Assert.Equal("subscribe", resubscribe.Op);
        Assert.Equal("order", resubscribe.Arg.GetProperty("topic").GetString());
    }

    private static BitgetSpotWsClient CreateClient(
        FakeWsTransport transport, BitgetCredentials? credentials, TimeSpan? pingInterval = null) =>
        new(new Uri("wss://localhost/ws/private"),
            () => transport,
            _ => { },
            ReconnectImmediately,
            pingInterval ?? TimeSpan.FromHours(1), // 默认关掉 ping 干扰
            credentials);

    // 测试用立即重连，替代基类退避策略以免拖慢用例
    private static async Task ReconnectImmediately(Func<CancellationToken, Task> connect, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await connect(ct);
                return;
            }
            catch (Exception) when (attempt < 50)
            {
                await Task.Delay(10, ct);
            }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.True(condition(), $"Timed out waiting for {description}.");
    }

    private static (string Op, JsonElement Arg) ParseFrame(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (
            root.GetProperty("op").GetString()!,
            root.GetProperty("args").EnumerateArray().Single().Clone());
    }

    private sealed class Collector<T> : IObserver<T>
    {
        public List<T> Items { get; } = [];
        public List<Exception> Errors { get; } = [];

        public void OnNext(T value)
        {
            lock (Items)
                Items.Add(value);
        }

        public void OnError(Exception error)
        {
            lock (Errors)
                Errors.Add(error);
        }

        public void OnCompleted() { }
    }

    private sealed class FakeWsTransport : IWsTransport
    {
        private readonly Lock _gate = new();
        private readonly Queue<string?> _inbound = new();
        private readonly SemaphoreSlim _signal = new(0);

        public List<string> SentFrames { get; } = [];
        public int ConnectCount { get; private set; }

        public Task ConnectAsync(Uri endpoint, CancellationToken ct)
        {
            ConnectCount++;
            return Task.CompletedTask;
        }

        public Task SendAsync(string message, CancellationToken ct)
        {
            lock (_gate)
                SentFrames.Add(message);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct);
            lock (_gate)
                return _inbound.Dequeue();
        }

        /// <summary>回放一条入站消息；null 表示服务端关闭连接</summary>
        public void Push(string? message)
        {
            lock (_gate)
                _inbound.Enqueue(message);
            _signal.Release();
        }

        // login 失败时真实服务端会断连；Abort 视同服务端断连，让接收循环退出进入重连
        public void Abort() => Push(null);

        public void Dispose() { }
    }
}
