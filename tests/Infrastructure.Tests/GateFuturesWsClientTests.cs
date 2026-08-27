using System.Reactive.Linq;
using System.Text.Json;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Common;
using TradingClient.Exchanges.Gate.WebSocket;

namespace TradingClient.Infrastructure.Tests;

// 结构与 GateSpotWsClientTests 对齐：假传输回放 + 引用计数 + 重连补订阅
public class GateFuturesWsClientTests
{
    private static readonly PerpetualFuturesSymbol BtcUsdt = new("BTC", "USDT");

    private static decimal GetMultiplier(string contract) =>
        contract == "BTC_USDT"
            ? 0.0001m
            : throw new NotSupportedException($"Unknown contract '{contract}'.");

    [Fact]
    public async Task SubscribeQuotes_FirstSubscription_ConnectsAndSendsSubscribeFrame()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());

        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");
        var frame = ParseFrame(transport.SentFrames[0]);
        Assert.Equal("futures.book_ticker", frame.Channel);
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
        Assert.Equal("futures.book_ticker", frame.Channel);
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
    public async Task SubscribeOrderBook_Always_SendsIntervalAndLevelInPayload()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);

        using var sub = client.SubscribeOrderBook(BtcUsdt).Subscribe(new Collector<OrderBookDelta>());

        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");
        var frame = ParseFrame(transport.SentFrames[0]);
        Assert.Equal("futures.order_book_update", frame.Channel);
        Assert.Equal(["BTC_USDT", "100ms", "100"], frame.Payload);
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

    // 推送帧样本：2026-08-27 录自 testnet futures.book_ticker
    [Fact]
    public async Task BookTickerUpdate_AfterSubscribe_DispatchesMappedQuote()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<Quote>();

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push("""
            {
              "time": 1787789060,
              "time_ms": 1787789060341,
              "channel": "futures.book_ticker",
              "event": "update",
              "result": {
                "t": 1787789060341,
                "u": 82069777678,
                "s": "BTC_USDT",
                "b": "79027",
                "B": 53105,
                "a": "79364.9",
                "A": 1
              }
            }
            """);

        await WaitForAsync(() => collector.Items.Count == 1, "quote");
        var quote = collector.Items[0];
        Assert.Equal(BtcUsdt, quote.Symbol);
        Assert.Equal(79027m, quote.BestBid);
        Assert.Equal(79364.9m, quote.BestAsk);
    }

    // 出处同上 trades notification 示例（整数形态）；负 size=主动卖，张×乘数=币
    [Fact]
    public async Task TradesUpdate_AfterSubscribe_DispatchesMappedTradeInCoins()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<Trade>();

        using var sub = client.SubscribeTrades(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push("""
            {
              "channel": "futures.trades",
              "event": "update",
              "time": 1541503698,
              "time_ms": 1541503698123,
              "result": [
                {
                  "size": -108,
                  "id": 27753479,
                  "create_time": 1545136464,
                  "create_time_ms": 1545136464123,
                  "price": "96.4",
                  "contract": "BTC_USDT"
                }
              ]
            }
            """);

        await WaitForAsync(() => collector.Items.Count == 1, "trade");
        var trade = collector.Items[0];
        Assert.Equal(BtcUsdt, trade.Symbol);
        Assert.Equal(OrderSide.Sell, trade.Side);
        Assert.Equal(0.0108m, trade.Quantity);
    }

    // 出处同上 order book update notification 示例（full=true 快照，档位量张→币）
    [Fact]
    public async Task OrderBookUpdate_AfterSubscribe_DispatchesMappedSnapshot()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<OrderBookDelta>();

        using var sub = client.SubscribeOrderBook(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push("""
            {
              "time": 1615366381,
              "time_ms": 1615366381123,
              "channel": "futures.order_book_update",
              "event": "update",
              "result": {
                "t": 1615366381417,
                "full": true,
                "s": "BTC_USDT",
                "U": 2517661101,
                "u": 2517661113,
                "b": [{ "p": "54664.5", "s": 58794 }],
                "a": [{ "p": "54743.6", "s": 0 }],
                "l": "100"
              }
            }
            """);

        await WaitForAsync(() => collector.Items.Count == 1, "order book delta");
        var delta = collector.Items[0];
        Assert.Equal(BtcUsdt, delta.Symbol);
        Assert.True(delta.IsSnapshot);
        Assert.Equal([new OrderBookLevel(54664.5m, 5.8794m)], delta.Bids);
        // s=0 透传（删档由上层处理）
        Assert.Equal([new OrderBookLevel(54743.6m, 0m)], delta.Asks);
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
              "time": 1545404023,
              "time_ms": 1545404023123,
              "channel": "futures.book_ticker",
              "event": "subscribe",
              "error": null,
              "result": { "status": "success" }
            }
            """);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(collector.Items);
    }

    [Fact]
    public async Task SubscribeAck_WithError_NotifiesSubscriberOnError()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport);
        var collector = new Collector<Quote>();

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(collector);
        await WaitForAsync(() => transport.SentFrames.Count == 1, "subscribe frame");

        transport.Push("""
            {
              "time": 1545404023,
              "time_ms": 1545404023123,
              "channel": "futures.book_ticker",
              "event": "subscribe",
              "error": { "code": 2, "message": "Invalid contract" },
              "result": null
            }
            """);

        await WaitForAsync(() => collector.Errors.Count == 1, "ack error notification");
        Assert.Contains("Invalid contract", collector.Errors[0].Message);
    }

    [Fact]
    public async Task PingLoop_AfterInterval_SendsFuturesPingFrame()
    {
        var transport = new FakeWsTransport();
        await using var client = CreateClient(transport, pingInterval: TimeSpan.FromMilliseconds(50));

        using var sub = client.SubscribeQuotes(BtcUsdt).Subscribe(new Collector<Quote>());

        await WaitForAsync(
            () => transport.SentFrames.Any(f => f.Contains("futures.ping")),
            "futures.ping frame");
    }

    private static GateFuturesWsClient CreateClient(
        FakeWsTransport transport, TimeSpan? pingInterval = null) =>
        new(new Uri("wss://localhost/futures/ws"),
            () => transport,
            _ => { },
            ReconnectImmediately,
            GetMultiplier,
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
