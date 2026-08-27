using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Risk;

/// <summary>
/// 事中风险监控（§6.4 第二层）：订阅持仓/行情/连接状态流，持续重建 RiskSnapshot 喂评估器，
/// 取最重态驱动 RiskStateMachine（与事前链的 RiskStateGateRule 咬合）。
/// 恢复语义：Warning / ReduceOnly 指标回落自动降级；Locked 不自动降级（有意），
/// 由人工 RiskStateMachine.TransitionTo(Normal) 复位。
/// kill switch：进入 Locked（KillSwitchOnLocked）或断连（KillSwitchOnDisconnect）时撤全部在途单，
/// 各按 episode 只触发一次；撤单走 REST 不依赖 WS 推送链路。自动减仓不接，是有意的扩展点（§6.4 可选）。
/// </summary>
public sealed class RiskMonitor : IDisposable, IRiskSnapshotSource
{
    private readonly IFuturesTrading _trading;
    private readonly IMarketData? _marketData;
    private readonly RiskStateMachine _stateMachine;
    private readonly IReadOnlyList<IRiskEvaluator> _evaluators;
    private readonly IRiskAuditSink _audit;
    private readonly RiskMonitorConfig _config;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private readonly Dictionary<(string SymbolRaw, PositionSide Side), Position> _positions = new();
    private readonly Dictionary<string, decimal> _latestPrices = new();
    private readonly Dictionary<string, IDisposable> _quoteSubscriptions = new();
    // history_pnl 是合约生命周期累计已实现盈亏（无日切字段）：按 Symbol 跟踪当前值与日初基线，
    // 当日已实现 = 差分近似；监控启动前 / Symbol 首次出现前的当日已实现不可知（口径见 RiskSnapshot）
    private readonly Dictionary<string, decimal> _realizedBySymbol = new();
    private readonly Dictionary<string, decimal> _realizedBaselines = new();
    private DateOnly _currentDay;
    private bool _disconnectKillFired;
    private bool _lockedKillFired;

    private IDisposable? _positionSubscription;
    private IDisposable? _connectionSubscription;

    public RiskMonitor(
        IFuturesTrading trading,
        IMarketData? marketData,
        RiskStateMachine stateMachine,
        IReadOnlyList<IRiskEvaluator> evaluators,
        IRiskAuditSink audit,
        RiskMonitorConfig config,
        TimeProvider time)
    {
        _trading = trading;
        _marketData = marketData;
        _stateMachine = stateMachine;
        _evaluators = evaluators;
        _audit = audit;
        _config = config;
        _time = time;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_positionSubscription is not null)
                return;
            _currentDay = CurrentDay();
            _positionSubscription = _trading.PositionUpdates.Subscribe(OnPositionUpdate);
            _connectionSubscription = _trading.ConnectionStates.Subscribe(OnConnectionState);
        }
    }

    // IRiskSnapshotSource：事前链下单时实时查，与 Reevaluate 共用同一把锁，读到的是一致快照
    public decimal? GetLatestPrice(Symbol symbol)
    {
        lock (_gate)
            return _latestPrices.TryGetValue(symbol.Raw, out var price) ? price : null;
    }

    public decimal? GetCurrentPositionQuantity(Symbol symbol)
    {
        lock (_gate)
        {
            // 带符号净额：Quantity 恒为绝对值、方向由 Side 携带（单边模式的空头也被适配器映射为 Short，§7）
            decimal net = 0m;
            var found = false;
            foreach (var position in _positions.Values)
            {
                if (position.Symbol.Raw != symbol.Raw)
                    continue;
                net += position.Side == PositionSide.Short ? -position.Quantity : position.Quantity;
                found = true;
            }
            return found ? net : null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _positionSubscription?.Dispose();
            _connectionSubscription?.Dispose();
            foreach (var subscription in _quoteSubscriptions.Values)
                subscription.Dispose();
            _quoteSubscriptions.Clear();
        }
    }

    private void OnPositionUpdate(PositionUpdate update)
    {
        lock (_gate)
        {
            var position = update.Position;
            var key = (SymbolRaw: position.Symbol.Raw, position.Side);
            if (position.Quantity == 0m)
            {
                _positions.Remove(key);
                // 同一 Symbol 的其他腿（dual 模式）还在则保留行情订阅
                if (!_positions.Keys.Any(k => k.SymbolRaw == key.SymbolRaw))
                {
                    if (_quoteSubscriptions.Remove(key.SymbolRaw, out var subscription))
                        subscription.Dispose();
                    _latestPrices.Remove(key.SymbolRaw);
                }
            }
            else
            {
                if (!_positions.ContainsKey(key))
                    EnsureQuoteSubscription(position.Symbol);
                _positions[key] = position;
            }

            if (position.RealizedPnl is { } realized)
            {
                _realizedBySymbol[key.SymbolRaw] = realized;
                // 首次见到的 Symbol 以当前累计值为基线（出现前的当日已实现不可知，差分近似的一部分）
                _realizedBaselines.TryAdd(key.SymbolRaw, realized);
            }

            RollDayIfNeeded();
            Reevaluate();
        }
    }

    private void OnQuote(Quote quote)
    {
        lock (_gate)
        {
            // 最新价取最优买卖中价：Quote 无 last 字段，做市终端口径中价更稳
            _latestPrices[quote.Symbol.Raw] = (quote.BestBid + quote.BestAsk) / 2m;
            RollDayIfNeeded();
            Reevaluate();
        }
    }

    private void OnConnectionState(ConnectionState state)
    {
        if (state == ConnectionState.Connected)
        {
            lock (_gate)
                _disconnectKillFired = false;
            return;
        }
        if (state != ConnectionState.Disconnected)
            return;

        lock (_gate)
        {
            if (!_config.KillSwitchOnDisconnect || _disconnectKillFired)
                return;
            _disconnectKillFired = true;
        }
        // 客户端断连时的 kill switch：撤单走 REST，与 WS 推送链路解耦，断连时仍可用
        FireKillSwitch("Disconnect");
    }

    private void EnsureQuoteSubscription(Symbol symbol)
    {
        // 无行情源时浮动盈亏恒估为 0（退化口径，RiskSnapshot 注释）
        if (_marketData is null || _quoteSubscriptions.ContainsKey(symbol.Raw))
            return;
        _quoteSubscriptions[symbol.Raw] = _marketData.SubscribeQuotes(symbol).Subscribe(OnQuote);
    }

    private void RollDayIfNeeded()
    {
        var day = CurrentDay();
        if (day == _currentDay)
            return;
        _currentDay = day;
        // 日切：基线重置为当前累计值，当日已实现归零重计（基线差分近似的日维度来源）
        foreach (var (symbol, realized) in _realizedBySymbol)
            _realizedBaselines[symbol] = realized;
    }

    private DateOnly CurrentDay() =>
        DateOnly.FromDateTime((_time.GetUtcNow() + _config.DayCutOffset).DateTime);

    private void Reevaluate()
    {
        var snapshot = BuildSnapshot();
        // 枚举声明序即严重度（Normal < Warning < ReduceOnly < Locked），取最重态
        var worst = _evaluators
            .Select(evaluator => evaluator.Evaluate(snapshot))
            .OfType<RiskAssessment>()
            .OrderByDescending(assessment => assessment.DesiredState)
            .FirstOrDefault();

        var current = _stateMachine.Current;
        // 离开 Locked 后重置 episode 标记，下一次进入要重新触发 kill switch
        if (current != RiskState.Locked)
            _lockedKillFired = false;
        // Locked 不自动降级（有意）：人工 TransitionTo(Normal) 复位，指标回落也不动
        if (current == RiskState.Locked)
            return;

        var target = worst?.DesiredState ?? RiskState.Normal;
        if (target == current)
            return;

        _stateMachine.TransitionTo(target, worst?.Reason ?? "All risk evaluators clear.");

        if (target == RiskState.Locked && _config.KillSwitchOnLocked && !_lockedKillFired)
        {
            _lockedKillFired = true;
            FireKillSwitch("Locked");
        }
    }

    private RiskSnapshot BuildSnapshot()
    {
        var dailyRealized = 0m;
        foreach (var (symbol, realized) in _realizedBySymbol)
            dailyRealized += realized - _realizedBaselines.GetValueOrDefault(symbol);

        var unrealized = 0m;
        var exposure = 0m;
        foreach (var position in _positions.Values)
        {
            // 无最新价的 Symbol 退化为开仓价：浮动盈亏计 0、敞口按 |数量| × 开仓价估（口径见 RiskSnapshot）
            var hasLatest = _latestPrices.TryGetValue(position.Symbol.Raw, out var latest);
            var mark = hasLatest ? latest : position.EntryPrice;
            if (hasLatest)
                unrealized += (latest - position.EntryPrice) * position.Quantity
                    * (position.Side == PositionSide.Short ? -1m : 1m);
            exposure += position.Quantity * mark;
        }

        return new RiskSnapshot(
            _positions.Values.ToArray(),
            new Dictionary<string, decimal>(_latestPrices),
            dailyRealized,
            unrealized,
            exposure,
            _time.GetUtcNow());
    }

    private void FireKillSwitch(string trigger)
    {
        // fire-and-forget：监控回路不阻塞在 REST 往返上，结果经审计出口回报
        _ = ExecuteKillSwitchAsync(trigger);
    }

    private async Task ExecuteKillSwitchAsync(string trigger)
    {
        string? errorCode = null;
        try
        {
            var result = await _trading.CancelAllFuturesOrdersAsync(CancellationToken.None);
            if (!result.IsSuccess)
                errorCode = result.Error!.Code;
        }
        catch (Exception)
        {
            errorCode = "KILL_SWITCH_EXCEPTION";
        }
        _audit.RecordKillSwitch(new RiskKillSwitchAction(trigger, errorCode is null, errorCode, _time.GetUtcNow()));
    }
}
