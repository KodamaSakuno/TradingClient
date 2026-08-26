using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Bitget.Models;
using TradingClient.Exchanges.Common;

namespace TradingClient.Exchanges.Bitget.WebSocket;

/// <summary>
/// Bitget UTA 现货公共行情 WS（私有频道 login/订单推送留待下一步）
/// </summary>
internal sealed class BitgetSpotWsClient : IAsyncDisposable
{
    // 协议要求每 30 秒发字面量 ping；服务端 2 分钟收不到 ping 即断连
    internal static readonly TimeSpan s_defaultPingInterval = TimeSpan.FromSeconds(30);

    private readonly Uri _endpoint;
    private readonly Func<IWsTransport> _transportFactory;
    private readonly Action<ConnectionState> _reportState;
    private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task> _reconnect;
    private readonly TimeSpan _pingInterval;

    private readonly Lock _gate = new();
    private readonly Dictionary<SubscriptionKey, SubscriptionEntry> _entries = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private CancellationTokenSource? _sessionCts;
    private Task? _supervisor;
    private IWsTransport? _transport;
    private CancellationTokenSource? _pingCts;
    private bool _connectedOnce;
    private bool _disposed;

    // TODO: 单连接限 10 msg/s、建议订阅 ≤50 频道；超限时需连接分片，本步不实现（订阅量级远未触及）
    public BitgetSpotWsClient(
        Uri endpoint,
        Func<IWsTransport> transportFactory,
        Action<ConnectionState> reportState,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> reconnect,
        TimeSpan? pingInterval = null)
    {
        _endpoint = endpoint;
        _transportFactory = transportFactory;
        _reportState = reportState;
        _reconnect = reconnect;
        _pingInterval = pingInterval ?? s_defaultPingInterval;
    }

    public IObservable<Quote> SubscribeQuotes(SpotSymbol symbol) =>
        Subscribe(BitgetWsProtocol.TopicTicker, BitgetSymbolFormatter.FormatSpot(symbol), BitgetWsProtocol.ToQuote);

    public IObservable<Trade> SubscribeTrades(SpotSymbol symbol) =>
        // 一条推送的 data 可含多笔成交，映射为数组后展开
        Subscribe(BitgetWsProtocol.TopicPublicTrade, BitgetSymbolFormatter.FormatSpot(symbol), BitgetWsProtocol.ToTrades)
            .SelectMany(trades => trades);

    public IObservable<OrderBookDelta> SubscribeOrderBook(SpotSymbol symbol) =>
        Subscribe(BitgetWsProtocol.TopicBooks, BitgetSymbolFormatter.FormatSpot(symbol), BitgetWsProtocol.ToOrderBookDelta);

    private IObservable<T> Subscribe<T>(
        string topic, string formattedSymbol, Func<BitgetWsEnvelope, T?> map) where T : class
    {
        var key = new SubscriptionKey(topic, formattedSymbol);

        return Observable.Create<T>(observer =>
        {
            var (entry, startSession) = Register(key);
            if (startSession)
                StartSupervisor();
            else
                SendSubscribeIfConnected(key, entry);

            var inner = entry.Updates.Subscribe(
                envelope =>
                {
                    T? value;
                    try
                    {
                        value = map(envelope);
                    }
                    catch (Exception)
                    {
                        // 坏消息跳过，不断流
                        return;
                    }

                    if (value is not null)
                        observer.OnNext(value);
                },
                observer.OnError,
                observer.OnCompleted);

            return Disposable.Create(() =>
            {
                inner.Dispose();
                Unregister(key, entry);
            });
        });
    }

    private (SubscriptionEntry Entry, bool StartSession) Register(SubscriptionKey key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new SubscriptionEntry();
                _entries.Add(key, entry);
            }

            entry.RefCount++;

            var startSession = _sessionCts is null;
            if (startSession)
                _sessionCts = new CancellationTokenSource();

            return (entry, startSession);
        }
    }

    private void Unregister(SubscriptionKey key, SubscriptionEntry entry)
    {
        bool lastForKey;
        lock (_gate)
        {
            lastForKey = --entry.RefCount == 0;
            if (lastForKey)
                _entries.Remove(key);
        }

        if (lastForKey)
        {
            entry.Updates.OnCompleted();
            SendIfConnected(BitgetWsProtocol.BuildUnsubscribeFrame(key.Topic, key.Symbol));
        }
    }

    private void StartSupervisor()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_supervisor is not null || _sessionCts is null)
                return;

            token = _sessionCts.Token;
            _supervisor = Task.Run(() => SuperviseAsync(token));
        }
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_connectedOnce)
                {
                    _reportState(ConnectionState.Connecting);
                    await ConnectSessionAsync(ct);
                    _connectedOnce = true;
                }
                else
                {
                    // 重连走基类退避策略（ReconnectAsync 内部会置 Reconnecting 状态）
                    await _reconnect(ConnectSessionAsync, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // 本轮重连次数耗尽，外层循环开启新一轮（仍有退避，非忙等）
                continue;
            }

            try
            {
                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // 接收循环异常视同断线，进入下一轮重连
            }
            finally
            {
                CleanupSession(ct);
            }
        }
    }

    private async Task ConnectSessionAsync(CancellationToken ct)
    {
        var transport = _transportFactory();
        await transport.ConnectAsync(_endpoint, ct);

        lock (_gate)
            _transport = transport;

        // 连接（重连）后重发全部活跃订阅
        List<(SubscriptionKey Key, SubscriptionEntry Entry)> active;
        lock (_gate)
            active = _entries.Where(p => p.Value.RefCount > 0).Select(p => (p.Key, p.Value)).ToList();

        foreach (var (key, entry) in active)
        {
            // 与 SendSubscribeIfConnected 约定：谁先把 Subscribed 翻成 true 谁负责发送，防止两条路径重复发 subscribe
            lock (_gate)
            {
                if (entry.Subscribed)
                    continue;

                entry.Subscribed = true;
            }

            await SendSafeAsync(transport, BitgetWsProtocol.BuildSubscribeFrame(key.Topic, key.Symbol));
        }

        _reportState(ConnectionState.Connected);
        StartPingLoop(transport);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            IWsTransport transport;
            lock (_gate)
                transport = _transport ?? throw new InvalidOperationException("No active transport.");

            var message = await transport.ReceiveAsync(ct);
            if (message is null)
                return;

            Dispatch(message);
        }
    }

    private void Dispatch(string message)
    {
        // 心跳响应是字面量 "pong" 文本帧，必须先于 JSON 解析判断
        if (BitgetWsProtocol.IsPong(message))
            return;

        var envelope = BitgetWsProtocol.ParseEnvelope(message);
        if (envelope is null)
            return;

        if (envelope.Event is not null)
        {
            // 订阅/退订 ack 成功无需处理；error ack 路由给该频道全部订阅者，不得静默丢弃（Gate 同款坑）
            if (BitgetWsProtocol.IsErrorAck(envelope))
            {
                List<SubscriptionEntry> affected;
                lock (_gate)
                    // error ack 一般带 arg（按 topic 定位）；不带 arg 的错误（如 login fail）广播给全部订阅者
                    affected = _entries
                        .Where(p => envelope.Arg is null || p.Key.Topic == envelope.Arg.Topic)
                        .Select(p => p.Value)
                        .ToList();

                foreach (var affectedEntry in affected)
                    affectedEntry.Updates.OnError(new InvalidOperationException(
                        $"Bitget WS subscription rejected on {envelope.Arg?.Topic ?? "unknown"}: [{envelope.Code}] {envelope.Msg}"));
            }

            return;
        }

        if (envelope.Arg is null)
            return;

        SubscriptionEntry? entry;
        lock (_gate)
            _entries.TryGetValue(new SubscriptionKey(envelope.Arg.Topic, envelope.Arg.Symbol), out entry);

        entry?.Updates.OnNext(envelope);
    }

    private void StartPingLoop(IWsTransport transport)
    {
        var pingCts = new CancellationTokenSource();
        lock (_gate)
        {
            _pingCts?.Cancel();
            _pingCts?.Dispose();
            _pingCts = pingCts;
        }

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_pingInterval);
            try
            {
                // 字面量 ping 文本帧（非 JSON），是 Bitget 唯一的保活手段
                while (await timer.WaitForNextTickAsync(pingCts.Token))
                    await SendSafeAsync(transport, BitgetWsProtocol.PingText);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CleanupSession(CancellationToken sessionToken)
    {
        IWsTransport? transport;
        CancellationTokenSource? pingCts;
        lock (_gate)
        {
            transport = _transport;
            _transport = null;
            pingCts = _pingCts;
            _pingCts = null;
        }

        pingCts?.Cancel();
        pingCts?.Dispose();
        transport?.Dispose();

        lock (_gate)
            foreach (var entry in _entries.Values)
                entry.Subscribed = false;

        if (!sessionToken.IsCancellationRequested)
            _reportState(ConnectionState.Disconnected);
    }

    private void SendSubscribeIfConnected(SubscriptionKey key, SubscriptionEntry entry)
    {
        IWsTransport? transport;
        lock (_gate)
        {
            // 建连竞态：未连接时交给连接后的重订阅循环；已连接且未订阅时才立即发送
            if (entry.Subscribed || entry.RefCount == 0)
                return;

            transport = _transport;
            if (transport is not null)
                entry.Subscribed = true;
        }

        if (transport is not null)
            _ = SendSafeAsync(transport, BitgetWsProtocol.BuildSubscribeFrame(key.Topic, key.Symbol));
    }

    private void SendIfConnected(string frame)
    {
        IWsTransport? transport;
        lock (_gate)
            transport = _transport;

        if (transport is not null)
            _ = SendSafeAsync(transport, frame);
    }

    // 发送失败由接收循环发现断线并重连，重连后统一补发订阅，这里不再重试
    private async Task SendSafeAsync(IWsTransport transport, string frame)
    {
        await _sendLock.WaitAsync();
        try
        {
            await transport.SendAsync(frame, CancellationToken.None);
        }
        catch (Exception)
        {
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? session;
        Task? supervisor;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            session = _sessionCts;
            _sessionCts = null;
            supervisor = _supervisor;
        }

        session?.Cancel();
        if (supervisor is not null)
        {
            try
            {
                await supervisor;
            }
            catch (OperationCanceledException)
            {
            }
        }

        CleanupSession(session?.Token ?? CancellationToken.None);
        session?.Dispose();
        _sendLock.Dispose();
        _reportState(ConnectionState.Disconnected);
    }

    private sealed record SubscriptionKey(string Topic, string Symbol);

    private sealed class SubscriptionEntry
    {
        public int RefCount;

        /// <summary>当前连接上是否已发送 subscribe（断线清理时重置）</summary>
        public bool Subscribed;
        public Subject<BitgetWsEnvelope> Updates { get; } = new();
    }
}
