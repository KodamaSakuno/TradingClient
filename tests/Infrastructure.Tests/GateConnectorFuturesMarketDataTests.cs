using System.Net;
using System.Text;
using System.Text.Json;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Common;
using TradingClient.Exchanges.Gate;

namespace TradingClient.Infrastructure.Tests;

// GateConnector 的现货/期货 WS 路由与张→币乘数缓存（§7）接线测试
public class GateConnectorFuturesMarketDataTests
{
    private static readonly SpotSymbol BtcUsdtSpot = new("BTC", "USDT");
    private static readonly PerpetualFuturesSymbol BtcUsdtPerp = new("BTC", "USDT");

    // 内嵌 JSON 仿 testnet fixture 结构（出处同 GateConnectorFuturesInstrumentsTests）
    private const string ContractsJson = """
        [
          {
            "name": "BTC_USDT",
            "quanto_multiplier": "0.0001",
            "order_price_round": "0.1",
            "order_size_min": 1,
            "order_size_max": 1000000,
            "enable_decimal": false,
            "status": "trading",
            "in_delisting": false
          }
        ]
        """;

    [Fact]
    public async Task SubscribeQuotes_WithPerpetualAndColdCache_FetchesContractsThenSendsSubscribeFrame()
    {
        var futuresTransport = new FakeWsTransport();
        var connector = CreateConnector(futuresTransport, out var requests);

        using var sub = connector.SubscribeQuotes(BtcUsdtPerp).Subscribe(new Collector<Quote>());

        await WaitForAsync(() => requests.Count == 1, "contracts request");
        Assert.Equal("/api/v4/futures/usdt/contracts", requests[0].RequestUri!.AbsolutePath);
        await WaitForAsync(() => futuresTransport.SentFrames.Count == 1, "subscribe frame");
        var frame = ParseFrame(futuresTransport.SentFrames[0]);
        Assert.Equal("futures.book_ticker", frame.Channel);
        Assert.Equal("subscribe", frame.Event);
        Assert.Equal(["BTC_USDT"], frame.Payload);
    }

    [Fact]
    public async Task SubscribeQuotes_WithCacheWarmedByGetInstruments_SkipsContractsRefetch()
    {
        var futuresTransport = new FakeWsTransport();
        var connector = CreateConnector(futuresTransport, out var requests);

        await connector.GetInstrumentsAsync(ProductKind.Futures, TestContext.Current.CancellationToken);
        Assert.Single(requests);

        using var sub = connector.SubscribeQuotes(BtcUsdtPerp).Subscribe(new Collector<Quote>());

        await WaitForAsync(() => futuresTransport.SentFrames.Count == 1, "subscribe frame");
        // 缓存已就绪，不再补拉 contracts
        Assert.Single(requests);
    }

    [Fact]
    public async Task SubscribeTrades_WithPerpetual_PushesConvertedToCoins()
    {
        var futuresTransport = new FakeWsTransport();
        var connector = CreateConnector(futuresTransport, out _);
        var collector = new Collector<Trade>();

        using var sub = connector.SubscribeTrades(BtcUsdtPerp).Subscribe(collector);
        await WaitForAsync(() => futuresTransport.SentFrames.Count == 1, "subscribe frame");

        // 推送帧出处：.local/gate_api_futures_p_ws.md 的 trades notification 示例（整数形态）
        futuresTransport.Push("""
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
        Assert.Equal(BtcUsdtPerp, trade.Symbol);
        // 负 size=主动卖；|108 张| × 0.0001 = 0.0108 币（§7：领域类型不出现张）
        Assert.Equal(OrderSide.Sell, trade.Side);
        Assert.Equal(0.0108m, trade.Quantity);
    }

    [Fact]
    public async Task SubscribeTrades_WithUnknownContractPush_SkipsBadFrameWithoutBreakingStream()
    {
        var futuresTransport = new FakeWsTransport();
        var connector = CreateConnector(futuresTransport, out _);
        var collector = new Collector<Trade>();

        using var sub = connector.SubscribeTrades(BtcUsdtPerp).Subscribe(collector);
        await WaitForAsync(() => futuresTransport.SentFrames.Count == 1, "subscribe frame");

        // 缓存里只有 BTC_USDT；未订阅合约的推送在路由阶段就找不到订阅项被丢弃（不进映射）
        futuresTransport.Push("""
            {
              "channel": "futures.trades",
              "event": "update",
              "time": 1541503698,
              "time_ms": 1541503698123,
              "result": [
                { "size": 5, "id": 1, "create_time_ms": 1545136464123, "price": "1", "contract": "BTC_USDT" }
              ]
            }
            """.Replace("BTC_USDT", "UNKNOWN_USDT"));
        // 坏消息之后的正常帧仍应送达，证明流未断
        futuresTransport.Push("""
            {
              "channel": "futures.trades",
              "event": "update",
              "time": 1541503698,
              "time_ms": 1541503698123,
              "result": [
                { "size": 5, "id": 2, "create_time_ms": 1545136464123, "price": "96.4", "contract": "BTC_USDT" }
              ]
            }
            """);

        await WaitForAsync(() => collector.Items.Count == 1, "good trade after bad frame");
        Assert.Equal("2", collector.Items[0].TradeId);
        Assert.Equal(0.0005m, collector.Items[0].Quantity);
        Assert.Empty(collector.Errors);
    }

    [Fact]
    public void GetQuantoMultiplier_WithUnknownContract_ThrowsNotSupported()
    {
        var connector = CreateConnector(new FakeWsTransport(), out _);

        Assert.Throws<NotSupportedException>(() => connector.GetQuantoMultiplier("BTC_USDT"));
    }

    [Fact]
    public async Task SubscribeQuotes_WithSpotSymbol_RoutesToSpotWsClient()
    {
        var spotTransport = new FakeWsTransport();
        var connector = CreateConnector(new FakeWsTransport(), out _, spotTransport);

        using var sub = connector.SubscribeQuotes(BtcUsdtSpot).Subscribe(new Collector<Quote>());

        await WaitForAsync(() => spotTransport.SentFrames.Count == 1, "spot subscribe frame");
        var frame = ParseFrame(spotTransport.SentFrames[0]);
        Assert.Equal("spot.tickers", frame.Channel);
        Assert.Equal(["BTC_USDT"], frame.Payload);
    }

    [Fact]
    public void SubscribeQuotes_WithDeliveryFuturesSymbol_ThrowsNotSupported()
    {
        var connector = CreateConnector(new FakeWsTransport(), out _);
        // 交割合约是另一族 WS 端点，本刀不接
        var delivery = new DeliveryFuturesSymbol("BTC", "USDT", new DateOnly(2026, 9, 25));

        Assert.Throws<NotSupportedException>(() => connector.SubscribeQuotes(delivery));
        Assert.Throws<NotSupportedException>(() => connector.SubscribeTrades(delivery));
        Assert.Throws<NotSupportedException>(() => connector.SubscribeOrderBook(delivery));
    }

    private static GateConnector CreateConnector(
        FakeWsTransport futuresTransport, out List<HttpRequestMessage> requests, FakeWsTransport? spotTransport = null)
    {
        var captured = new List<HttpRequestMessage>();
        requests = captured;
        return new GateConnector(
            new HttpClient(new StubHttpMessageHandler(request =>
            {
                lock (captured)
                    captured.Add(request);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ContractsJson, Encoding.UTF8, "application/json"),
                };
            })),
            GateConnector.DefaultBaseUrl,
            new Uri("ws://localhost/spot"),
            wsTransportFactory: () => spotTransport ?? throw new InvalidOperationException(),
            wsPingInterval: TimeSpan.FromHours(1), // 关掉 ping 干扰
            futuresWsEndpoint: new Uri("ws://localhost/futures"),
            futuresWsTransportFactory: () => futuresTransport);
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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
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

        public Task ConnectAsync(Uri endpoint, CancellationToken ct) => Task.CompletedTask;

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
