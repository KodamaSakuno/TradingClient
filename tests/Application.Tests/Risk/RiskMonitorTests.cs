using TradingClient.Application.Risk;
using TradingClient.Application.Risk.Evaluators;
using TradingClient.Application.Tests.Fakes;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Risk;

public class RiskMonitorTests
{
    private static readonly PerpetualFuturesSymbol BtcUsdt = new("BTC", "USDT");

    // 阈值刻意拉开：敞口用例不碰亏损档，亏损用例不碰敞口档
    private static RiskMonitorConfig Config(bool killSwitchOnLocked = true, bool killSwitchOnDisconnect = true) =>
        new(
            DailyLossWarning: 100m,
            DailyLossReduceOnly: 200m,
            DailyLossLocked: 300m,
            ExposureWarning: 1_000m,
            ExposureReduceOnly: 2_000m,
            KillSwitchOnLocked: killSwitchOnLocked,
            KillSwitchOnDisconnect: killSwitchOnDisconnect,
            DayCutOffset: TimeSpan.FromHours(8));

    private sealed record Harness(
        RiskMonitor Monitor,
        FakeFuturesTrading Trading,
        FakeMarketData MarketData,
        RiskStateMachine StateMachine,
        FakeRiskAuditSink Audit,
        FakeTimeProvider Time);

    private static Harness Create(RiskMonitorConfig? config = null, DateTimeOffset? utcNow = null)
    {
        var trading = new FakeFuturesTrading();
        var marketData = new FakeMarketData();
        var audit = new FakeRiskAuditSink();
        var machine = new RiskStateMachine(audit);
        var time = new FakeTimeProvider(utcNow ?? new DateTimeOffset(2026, 8, 27, 20, 0, 0, TimeSpan.Zero));
        var monitor = new RiskMonitor(
            trading, marketData, machine,
            [new DailyLossCircuitBreaker(config ?? Config()), new TotalExposureLimitEvaluator(config ?? Config())],
            audit, config ?? Config(), time);
        monitor.Start();
        return new Harness(monitor, trading, marketData, machine, audit, time);
    }

    private static Position LongPosition(decimal quantity, decimal entry, decimal? realized = null) =>
        new(BtcUsdt, PositionSide.Long, quantity, entry, UnrealizedPnl: 0m, Leverage: 1, MarginMode.Cross, realized);

    private static void PushQuote(Harness h, decimal price) =>
        h.MarketData.PushQuote(new Quote(BtcUsdt, price, price, DateTimeOffset.UtcNow));

    // kill switch 是 fire-and-forget 的 REST 调用，断言需等异步落地
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
    public void PositionUpdate_ExposureEscalates_TransitionsUpThroughStates()
    {
        var h = Create(Config(killSwitchOnLocked: false));
        // entry 500、qty 1：无最新价时敞口退化按开仓价估 = 500，低于 Warning 档
        h.Trading.PushPosition(LongPosition(1m, 500m));
        Assert.Equal(RiskState.Normal, h.StateMachine.Current);

        PushQuote(h, 1_500m);
        Assert.Equal(RiskState.Warning, h.StateMachine.Current);

        PushQuote(h, 2_500m);
        Assert.Equal(RiskState.ReduceOnly, h.StateMachine.Current);
    }

    [Fact]
    public void PositionUpdate_MetricsFallBack_AutoDeescalates()
    {
        var h = Create(Config(killSwitchOnLocked: false));
        h.Trading.PushPosition(LongPosition(1m, 500m));
        PushQuote(h, 1_500m);
        Assert.Equal(RiskState.Warning, h.StateMachine.Current);

        PushQuote(h, 900m);
        Assert.Equal(RiskState.Normal, h.StateMachine.Current);
    }

    [Fact]
    public void LockedState_MetricsRecover_DoesNotAutoDeescalate()
    {
        var h = Create(Config(killSwitchOnLocked: false));
        h.Trading.PushPosition(LongPosition(1m, 500m));
        // 浮动亏损 (100 − 500) × 1 = −400，超 Locked 档
        PushQuote(h, 100m);
        Assert.Equal(RiskState.Locked, h.StateMachine.Current);

        // 指标回落也不自动降级：Locked 只能人工 TransitionTo(Normal) 复位
        PushQuote(h, 500m);
        Assert.Equal(RiskState.Locked, h.StateMachine.Current);
    }

    [Fact]
    public async Task EnteringLocked_WithKillSwitch_CancelsAllOrdersOncePerEpisode()
    {
        var h = Create();
        h.Trading.PushPosition(LongPosition(1m, 500m));
        PushQuote(h, 100m); // 浮动 −400 → Locked

        await WaitUntilAsync(() => h.Trading.CancelAllCallCount == 1);
        var action = Assert.Single(h.Audit.KillSwitchActions);
        Assert.Equal("Locked", action.Trigger);
        Assert.True(action.Succeeded);

        // 停留在 Locked 期间的后续评估不重复触发
        PushQuote(h, 90m);
        Assert.Equal(1, h.Trading.CancelAllCallCount);

        // 人工复位后再次进入 Locked 是新 episode，要重新触发
        h.StateMachine.TransitionTo(RiskState.Normal, "manual reset");
        PushQuote(h, 80m);
        await WaitUntilAsync(() => h.Trading.CancelAllCallCount == 2);
        Assert.Equal(RiskState.Locked, h.StateMachine.Current);
    }

    [Fact]
    public async Task Disconnect_WithKillSwitch_CancelsAllOrdersOncePerEpisode()
    {
        var h = Create();

        h.Trading.PushConnectionState(ConnectionState.Disconnected);
        await WaitUntilAsync(() => h.Trading.CancelAllCallCount == 1);
        Assert.Equal("Disconnect", Assert.Single(h.Audit.KillSwitchActions).Trigger);

        // 同一断连 episode 的重复断开事件不重复触发
        h.Trading.PushConnectionState(ConnectionState.Disconnected);
        Assert.Equal(1, h.Trading.CancelAllCallCount);

        // 重连后重置 episode：下一次断开重新触发
        h.Trading.PushConnectionState(ConnectionState.Connected);
        h.Trading.PushConnectionState(ConnectionState.Disconnected);
        await WaitUntilAsync(() => h.Trading.CancelAllCallCount == 2);
    }

    [Fact]
    public async Task Disconnect_KillSwitchDisabled_DoesNotCancel()
    {
        var h = Create(Config(killSwitchOnDisconnect: false));

        h.Trading.PushConnectionState(ConnectionState.Disconnected);
        await Task.Delay(100, TestContext.Current.CancellationToken); // 给 fire-and-forget 一个犯错的机会

        Assert.Equal(0, h.Trading.CancelAllCallCount);
    }

    [Fact]
    public void PositionUpdate_DayRollover_ResetsRealizedBaseline()
    {
        // 2026-08-27 20:00 UTC + 8h = 08-28 04:00，日切日 = 08-28
        var h = Create(Config(killSwitchOnLocked: false), utcNow: new DateTimeOffset(2026, 8, 27, 20, 0, 0, TimeSpan.Zero));

        // history_pnl 是生命周期累计：首次见到 −100 只 seed 基线，当日贡献为 0
        h.Trading.PushPosition(LongPosition(1m, 500m, realized: -100m));
        Assert.Equal(RiskState.Normal, h.StateMachine.Current);

        // 累计跌到 −250 → 当日已实现 −150，超 Warning 档
        h.Trading.PushPosition(LongPosition(1m, 500m, realized: -250m));
        Assert.Equal(RiskState.Warning, h.StateMachine.Current);

        // 跨日切（08-28 16:00 UTC + 8h = 08-29 00:00）：基线重置，当日已实现归零 → 回落 Normal
        h.Time.UtcNow = new DateTimeOffset(2026, 8, 28, 16, 0, 0, TimeSpan.Zero);
        h.Trading.PushPosition(LongPosition(1m, 500m, realized: -250m));
        Assert.Equal(RiskState.Normal, h.StateMachine.Current);
    }

    [Fact]
    public void PositionUpdate_QuantityZero_RemovesPositionAndUnsubscribesQuotes()
    {
        var h = Create(Config(killSwitchOnLocked: false));

        h.Trading.PushPosition(LongPosition(1m, 500m));
        Assert.Equal(1, h.MarketData.ActiveQuoteSubscriptions);

        h.Trading.PushPosition(LongPosition(0m, 500m));
        Assert.Equal(0, h.MarketData.ActiveQuoteSubscriptions);
    }

    [Fact]
    public void PositionUpdate_WithoutMarketData_UnrealizedEstimatedAsZero()
    {
        var trading = new FakeFuturesTrading();
        var audit = new FakeRiskAuditSink();
        var machine = new RiskStateMachine(audit);
        var config = Config(killSwitchOnLocked: false);
        using var monitor = new RiskMonitor(
            trading, marketData: null, machine,
            [new DailyLossCircuitBreaker(config), new TotalExposureLimitEvaluator(config)],
            audit, config, new FakeTimeProvider(DateTimeOffset.UtcNow));
        monitor.Start();

        // 无行情源：浮动盈亏恒估 0、敞口按开仓价（1 × 500 = 500），均不触发
        trading.PushPosition(LongPosition(1m, 500m));

        Assert.Equal(RiskState.Normal, machine.Current);
    }
}
