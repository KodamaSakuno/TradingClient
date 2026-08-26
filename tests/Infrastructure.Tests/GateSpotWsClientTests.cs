using System.Text.Json;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Gate.WebSocket;

namespace TradingClient.Infrastructure.Tests;

public class GateSpotWsClientTests
{
    private static readonly SpotSymbol BtcUsdt = new("BTC", "USDT");

    [Fact]
    public async Task SubscribeQuotes_FirstSubscription_ConnectsAndSendsSubscribeFrame()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());

        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");
        var frame = ParseFrame(transport.SentFrames[0]);
        Assert.Equal("spot.tickers", frame.Channel);
        Assert.Equal("subscribe", frame.Event);
        Assert.Equal(["BTC_USDT"], frame.Payload);
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
        var frame = ParseFrame(transport.SentFrames[1]);
        Assert.Equal("spot.tickers", frame.Channel);
        Assert.Equal("unsubscribe", frame.Event);
        Assert.Equal(["BTC_USDT"], frame.Payload);
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
    public async Task SubscribeOrderBook_Always_SendsIntervalInPayload()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);

        using var sub = client.SubscribeOrderBook(BtcUsdt).Subscribe(new Collector<OrderBookDelta>());

        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");
        var frame = ParseFrame(transport.SentFrames[0]);
        Assert.Equal("spot.order_book_update", frame.Channel);
        Assert.Equal(["BTC_USDT", "100ms"], frame.Payload);
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
        Assert.Equal(2, transport.SentFrames.Skip(2).Count(f => ParseFrame(f).Event == "subscribe"));
    }

    [Fact]
    public async Task TickerUpdate_AfterSubscribe_DispatchesMappedQuote()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<Quote>();

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push("""
            {
              "time": 1669107766,
              "time_ms": 1669107766406,
              "channel": "spot.tickers",
              "event": "update",
              "result": {
                "currency_pair": "BTC_USDT",
                "last": "15743.4",
                "lowest_ask": "15744.4",
                "highest_bid": "15743.5"
              }
            }
            """);

        await WaitForAsync(() => collector.Items.Count == 1, "quote");
        var quote = collector.Items[0];
        Assert.Equal(BtcUsdt, quote.Symbol);
        Assert.Equal(15743.5m, quote.BestBid);
        Assert.Equal(15744.4m, quote.BestAsk);
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
              "time": 1669107766,
              "time_ms": 1669107766406,
              "channel": "spot.tickers",
              "event": "subscribe",
              "error": null,
              "result": { "status": "success" }
            }
            """);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(collector.Items);
    }

    [Fact]
    public async Task PingLoop_AfterInterval_SendsSpotPingFrame()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport, pingInterval: TimeSpan.FromMilliseconds(50));

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());

        await WaitForAsync(
            () => transport.SentFrames.Any(f => f.Contains("spot.ping")),
            "spot.ping frame");
    }

    private static GateSpotWsClient CreateClient(FakeWsTransport transport, TimeSpan? pingInterval = null) =>
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

    private static (string Channel, string Event, string[] Payload) ParseFrame(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (
            root.GetProperty("channel").GetString()!,
            root.GetProperty("event").GetString()!,
            root.GetProperty("payload").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    private sealed class Collector<T> : IObserver<T>
    {
        public List<T> Items { get; } = [];

        public void OnNext(T value)
        {
            lock (Items)
                Items.Add(value);
        }

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }

    private sealed class FakeWsTransport : IGateWsTransport
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
