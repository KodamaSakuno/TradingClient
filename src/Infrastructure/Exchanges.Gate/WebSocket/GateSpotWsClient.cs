using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Common;
using TradingClient.Exchanges.Gate.Auth;
using TradingClient.Exchanges.Gate.Models;

namespace TradingClient.Exchanges.Gate.WebSocket;

/// <summary>
/// Gate 现货公共行情 WS
/// </summary>
internal sealed class GateSpotWsClient : IAsyncDisposable
{
    internal static readonly TimeSpan s_defaultPingInterval = TimeSpan.FromSeconds(20);

    private readonly Uri _endpoint;
    private readonly Func<IGateWsTransport> _transportFactory;
    private readonly Action<ConnectionState> _reportState;
    private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task> _reconnect;
    private readonly TimeSpan _pingInterval;
    private readonly GateCredentials? _credentials;
    private readonly ServerTimeSync? _timeSync;

    private readonly Lock _gate = new();
    private readonly Dictionary<SubscriptionKey, SubscriptionEntry> _entries = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private CancellationTokenSource? _sessionCts;
    private Task? _supervisor;
    private IGateWsTransport? _transport;
    private CancellationTokenSource? _pingCts;
    private bool _connectedOnce;
    private bool _disposed;

    public GateSpotWsClient(
        Uri endpoint,
        Func<IGateWsTransport> transportFactory,
        Action<ConnectionState> reportState,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> reconnect,
        TimeSpan? pingInterval = null,
        GateCredentials? credentials = null,
        ServerTimeSync? timeSync = null)
    {
        _endpoint = endpoint;
        _transportFactory = transportFactory;
        _reportState = reportState;
        _reconnect = reconnect;
        _pingInterval = pingInterval ?? s_defaultPingInterval;
        _credentials = credentials;
        _timeSync = timeSync;
    }

    public IObservable<Quote> SubscribeQuotes(SpotSymbol symbol) =>
        Subscribe(GateWsProtocol.ChannelTickers, GateSymbolFormatter.FormatSpot(symbol), [GateSymbolFormatter.FormatSpot(symbol)], GateWsProtocol.ToQuote);

    public IObservable<Trade> SubscribeTrades(SpotSymbol symbol) =>
        Subscribe(GateWsProtocol.ChannelTrades, GateSymbolFormatter.FormatSpot(symbol), [GateSymbolFormatter.FormatSpot(symbol)], GateWsProtocol.ToTrade);

    public IObservable<OrderBookDelta> SubscribeOrderBook(SpotSymbol symbol) =>
        Subscribe(
            GateWsProtocol.ChannelOrderBookUpdate,
            GateSymbolFormatter.FormatSpot(symbol),
            [GateSymbolFormatter.FormatSpot(symbol), GateWsProtocol.OrderBookInterval],
            GateWsProtocol.ToOrderBookDelta);

    public IObservable<SpotOrderUpdate> SubscribeSpotOrderUpdates()
    {
        // 私有频道无凭证必然被 Gate 拒（error code 4）：订阅时直接给错误，比连上后静默失败更明确
        if (_credentials is null)
            return Observable.Create<SpotOrderUpdate>(observer =>
            {
                observer.OnError(new InvalidOperationException("Gate private channels require credentials."));
                return Disposable.Empty;
            });

        // 一条通知可含多个订单，故按数组映射后再展开
        return Subscribe(
                GateWsProtocol.ChannelOrders,
                GateWsProtocol.OrdersAllPairs,
                [GateWsProtocol.OrdersAllPairs],
                GateWsProtocol.ToSpotOrderUpdates)
            .SelectMany(updates => updates);
    }

    // symbolKey 即订阅路由键：公共频道为交易对，spot.orders 固定为 "!all"（result 是数组，无 symbol 维度）
    private IObservable<T> Subscribe<T>(
        string channel, string symbolKey, string[] payload, Func<GateWsEnvelope, T?> map) where T : class
    {
        var key = new SubscriptionKey(channel, symbolKey);

        return Observable.Create<T>(observer =>
        {
            var (entry, startSession) = Register(key, payload);
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

    private (SubscriptionEntry Entry, bool StartSession) Register(SubscriptionKey key, string[] payload)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new SubscriptionEntry(payload);
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
            SendIfConnected(BuildRequestFrame(key.Channel, GateWsProtocol.EventUnsubscribe, entry.Payload));
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

        // 连接（重连）后重发全部活跃订阅；Gate 允许重复订阅，不覆盖已有订阅
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

            await SendSafeAsync(transport, BuildRequestFrame(
                key.Channel, GateWsProtocol.EventSubscribe, entry.Payload));
        }

        _reportState(ConnectionState.Connected);
        StartPingLoop(transport);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            IGateWsTransport transport;
            lock (_gate)
                transport = _transport ?? throw new InvalidOperationException("No active transport.");

            var message = await transport.ReceiveAsync(ct);
            if (message is null)
                return;

            Dispatch(message);
        }
    }

    private void Dispatch(string json)
    {
        var envelope = GateWsProtocol.ParseEnvelope(json);
        if (envelope is null || envelope.Channel is null)
            return;

        if (envelope.Channel == GateWsProtocol.ChannelPong)
            return;

        if (GateWsProtocol.IsUpgradeNotice(envelope))
        {
            // 服务端升级通告要求尽快重连；Abort 使接收循环出错退出，进入退避重连
            lock (_gate)
                _transport?.Abort();
            return;
        }

        if (envelope.Event != GateWsProtocol.EventUpdate)
            return; // subscribe/unsubscribe 的 ack 无需处理

        // spot.orders 的 result 是订单数组且按 !all 订阅，无 symbol 维度，直接按频道路由
        var symbol = envelope.Channel == GateWsProtocol.ChannelOrders
            ? GateWsProtocol.OrdersAllPairs
            : GateWsProtocol.ExtractSymbol(envelope);
        if (symbol is null)
            return;

        SubscriptionEntry? entry;
        lock (_gate)
            _entries.TryGetValue(new SubscriptionKey(envelope.Channel, symbol), out entry);

        entry?.Updates.OnNext(envelope);
    }

    private void StartPingLoop(IGateWsTransport transport)
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
                // 应用层 spot.ping：服务端收到后重置客户端超时计时器（协议层保活之外的附加手段）
                while (await timer.WaitForNextTickAsync(pingCts.Token))
                    await SendSafeAsync(transport, GateWsProtocol.BuildPingFrame());
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CleanupSession(CancellationToken sessionToken)
    {
        IGateWsTransport? transport;
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
        IGateWsTransport? transport;
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
            _ = SendSafeAsync(transport, BuildRequestFrame(
                key.Channel, GateWsProtocol.EventSubscribe, entry.Payload));
    }

    private string BuildRequestFrame(string channel, string evt, string[] payload)
    {
        if (!GateWsProtocol.IsPrivateChannel(channel))
            return GateWsProtocol.BuildRequestFrame(channel, evt, payload);

        // 私有频道请求体携带 auth；帧 time 与签名 time 必须一致，用校时后的时钟
        var timestamp = (_timeSync?.UtcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return GateWsProtocol.BuildAuthenticatedRequestFrame(channel, evt, payload, _credentials!, timestamp);
    }

    private void SendIfConnected(string frame)
    {
        IGateWsTransport? transport;
        lock (_gate)
            transport = _transport;

        if (transport is not null)
            _ = SendSafeAsync(transport, frame);
    }

    // 发送失败由接收循环发现断线并重连，重连后统一补发订阅，这里不再重试
    private async Task SendSafeAsync(IGateWsTransport transport, string frame)
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

    private sealed record SubscriptionKey(string Channel, string Symbol);

    private sealed class SubscriptionEntry(string[] payload)
    {
        public string[] Payload { get; } = payload;
        public int RefCount;

        /// <summary>当前连接上是否已发送 subscribe（断线清理时重置）</summary>
        public bool Subscribed;
        public Subject<GateWsEnvelope> Updates { get; } = new();
    }
}
