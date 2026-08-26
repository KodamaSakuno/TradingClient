using System.Text.Json;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Bitget.WebSocket;
using TradingClient.Exchanges.Common;

namespace TradingClient.Infrastructure.Tests;

public class BitgetSpotWsClientTests
{
    private static readonly SpotSymbol BtcUsdt = new("BTC", "USDT");

    // 推送帧样本取自官方文档示例（.local/bitget/uta/websocket/public/，2026-08 快照）
    private const string TickerPushJson = """
        {
          "data": [
            {
              "bid1Price": "99999",
              "ask1Size": "188.312553",
              "bid1Size": "186.183209",
              "ask1Price": "100000",
              "lastPrice": "100000"
            }
          ],
          "arg": { "instType": "spot", "symbol": "BTCUSDT", "topic": "ticker" },
          "action": "snapshot",
          "ts": 1736371332162
        }
        """;

    private const string PublicTradePushJson = """
        {
          "data": [
            {
              "p": "100000",
              "S": "buy",
              "T": "1736348770627",
              "v": "0.00118",
              "i": "1260903622036942849"
            }
          ],
          "arg": { "instType": "spot", "symbol": "BTCUSDT", "topic": "publicTrade" },
          "action": "snapshot",
          "ts": 1736371104297
        }
        """;

    private const string BooksSnapshotPushJson = """
        {
          "data": [
            {
              "a": [["99756.7", "23.9774"]],
              "b": [["99756.6", "0.0128"]],
              "pseq": 0,
              "seq": 1304314508780744705,
              "ts": "1746698732562"
            }
          ],
          "arg": { "instType": "spot", "symbol": "BTCUSDT", "topic": "books" },
          "action": "snapshot",
          "ts": 1746698732563
        }
        """;

    [Fact]
    public async Task SubscribeQuotes_FirstSubscription_ConnectsAndSendsSubscribeFrame()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());

        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");
        var (op, arg) = ParseFrame(transport.SentFrames[0]);
        Assert.Equal("subscribe", op);
        Assert.Equal("spot", arg.GetProperty("instType").GetString());
        Assert.Equal("ticker", arg.GetProperty("topic").GetString());
        Assert.Equal("BTCUSDT", arg.GetProperty("symbol").GetString());
    }

    [Fact]
    public async Task SubscribeQuotes_DuplicateSubscription_SendsSubscribeOnlyOnce()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);

        using var sub1 = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());
        using var sub2 = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());

        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Single(transport.SentFrames);
    }

    [Fact]
    public async Task Unsubscribe_LastSubscriber_SendsUnsubscribeFrame()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);

        var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        sub.Dispose();

        await WaitForAsync(() => transport.SentFrames.Count == 2, "unsubscribe frame");
        var (op, arg) = ParseFrame(transport.SentFrames[1]);
        Assert.Equal("unsubscribe", op);
        Assert.Equal("ticker", arg.GetProperty("topic").GetString());
        Assert.Equal("BTCUSDT", arg.GetProperty("symbol").GetString());
    }

    [Fact]
    public async Task Unsubscribe_WhileOtherSubscriberRemains_DoesNotUnsubscribe()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);

        var sub1 = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());
        using var sub2 = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        sub1.Dispose();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Single(transport.SentFrames);
    }

    [Fact]
    public async Task Reconnect_AfterServerClose_ResubscribesActiveSubscriptions()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);

        using var sub1 = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());
        using var sub2 = client.SubscribeTrades(BtcUsdt).Subscribe(new Collector<Trade>());
        await WaitForAsync(() => transport.SentFrames.Count == 2, "initial subscribe frames");

        transport.Push(null); // 服务端关闭连接

        await WaitForAsync(() => transport.ConnectCount == 2, "reconnect");
        await WaitForAsync(() => transport.SentFrames.Count == 4, "resubscribe frames");
        Assert.Equal(2, transport.SentFrames.Skip(2).Count(f => ParseFrame(f).Op == "subscribe"));
    }

    [Fact]
    public async Task SubscribeAck_AfterSubscribe_IsNotDispatched()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<Quote>();

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push("""
            {
              "event": "subscribe",
              "arg": { "instType": "spot", "topic": "ticker", "symbol": "BTCUSDT" },
              "connId": "xxxxxxxxxx"
            }
            """);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(collector.Items);
    }

    [Fact]
    public async Task ErrorAck_AfterSubscribe_NotifiesSubscriberOnError()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<Quote>();

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push("""
            {
              "event": "error",
              "arg": { "instType": "spot", "topic": "ticker", "symbol": "BTCUSDT" },
              "code": "30001",
              "msg": "topic is required"
            }
            """);

        await WaitForAsync(() => collector.Errors.Count == 1, "error ack notification");
        Assert.Contains("topic is required", collector.Errors[0].Message);
        Assert.Contains("30001", collector.Errors[0].Message);
    }

    [Fact]
    public async Task PongMessage_AfterSubscribe_IsIgnored()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<Quote>();

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push("pong");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(collector.Items);
        Assert.Empty(collector.Errors);
    }

    [Fact]
    public async Task PingLoop_AfterInterval_SendsLiteralPingFrame()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport, pingInterval: TimeSpan.FromMilliseconds(50));

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());

        await WaitForAsync(() => transport.SentFrames.Any(f => f == "ping"), "literal ping frame");
    }

    [Fact]
    public async Task TickerPush_AfterSubscribe_DispatchesMappedQuote()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<Quote>();

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push(TickerPushJson);

        await WaitForAsync(() => collector.Items.Count == 1, "quote");
        var quote = collector.Items[0];
        Assert.Equal(BtcUsdt, quote.Symbol);
        Assert.Equal(99999m, quote.BestBid);
        Assert.Equal(100000m, quote.BestAsk);
    }

    [Fact]
    public async Task PublicTradePush_AfterSubscribe_DispatchesMappedTrade()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<Trade>();

        using var sub = client.SubscribeTrades(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push(PublicTradePushJson);

        await WaitForAsync(() => collector.Items.Count == 1, "trade");
        var trade = collector.Items[0];
        Assert.Equal("1260903622036942849", trade.TradeId);
        Assert.Equal(100000m, trade.Price);
        Assert.Equal(OrderSide.Buy, trade.Side);
    }

    [Fact]
    public async Task BooksPush_AfterSubscribe_DispatchesMappedOrderBookDelta()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<OrderBookDelta>();

        using var sub = client.SubscribeOrderBook(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push(BooksSnapshotPushJson);

        await WaitForAsync(() => collector.Items.Count == 1, "order book delta");
        var delta = collector.Items[0];
        Assert.Equal(BtcUsdt, delta.Symbol);
        Assert.True(delta.IsSnapshot);
        Assert.Equal([new OrderBookLevel(99756.6m, 0.0128m)], delta.Bids);
        Assert.Equal([new OrderBookLevel(99756.7m, 23.9774m)], delta.Asks);
    }

    private static BitgetSpotWsClient CreateClient(FakeWsTransport transport, TimeSpan? pingInterval = null) =>
        new(new Uri("wss://localhost/ws"),
            () => transport,
            _ => { },
            ReconnectImmediately,
            pingInterval ?? TimeSpan.FromHours(1)); // 默认关掉 ping 干扰，单独用例再开

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

        public void Abort() { }

        public void Dispose() { }
    }
}
