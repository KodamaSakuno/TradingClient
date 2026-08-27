using TradingClient.Application.Risk;
using TradingClient.Application.Risk.Evaluators;

namespace TradingClient.Application.Tests.Risk;

public class DailyLossCircuitBreakerTests
{
    private static readonly RiskMonitorConfig Config = new(
        DailyLossWarning: 100m,
        DailyLossReduceOnly: 200m,
        DailyLossLocked: 300m,
        ExposureWarning: 1_000m,
        ExposureReduceOnly: 2_000m,
        KillSwitchOnLocked: true,
        KillSwitchOnDisconnect: true,
        DayCutOffset: TimeSpan.FromHours(8));

    private readonly DailyLossCircuitBreaker _evaluator = new(Config);

    private static RiskSnapshot Snapshot(decimal dailyRealized = 0m, decimal unrealized = 0m) =>
        new([], new Dictionary<string, decimal>(), dailyRealized, unrealized, 0m, DateTimeOffset.UtcNow);

    [Fact]
    public void Evaluate_ProfitableDay_DoesNotTrigger()
    {
        Assert.Null(_evaluator.Evaluate(Snapshot(dailyRealized: 500m)));
    }

    [Fact]
    public void Evaluate_LossExactlyAtWarningThreshold_DoesNotTrigger()
    {
        // 边界约定与 PriceDeviationRule 一致：严格超过才触发，恰好等于不触发
        Assert.Null(_evaluator.Evaluate(Snapshot(dailyRealized: -100m)));
    }

    [Fact]
    public void Evaluate_LossBeyondWarningThreshold_RequestsWarning()
    {
        var assessment = _evaluator.Evaluate(Snapshot(dailyRealized: -100.01m));

        Assert.NotNull(assessment);
        Assert.Equal(RiskState.Warning, assessment.DesiredState);
        Assert.Equal("DailyLossCircuitBreaker", assessment.EvaluatorName);
    }

    [Fact]
    public void Evaluate_RealizedPlusUnrealized_CombinesBoth()
    {
        // 单日亏损口径 = 当日已实现 + 浮动，各自不超但合计超 Warning
        var assessment = _evaluator.Evaluate(Snapshot(dailyRealized: -60m, unrealized: -50m));

        Assert.NotNull(assessment);
        Assert.Equal(RiskState.Warning, assessment.DesiredState);
    }

    [Fact]
    public void Evaluate_LossBeyondReduceOnlyThreshold_RequestsReduceOnly()
    {
        var assessment = _evaluator.Evaluate(Snapshot(dailyRealized: -200.01m));

        Assert.Equal(RiskState.ReduceOnly, assessment!.DesiredState);
    }

    [Fact]
    public void Evaluate_LossBeyondLockedThreshold_RequestsLocked()
    {
        var assessment = _evaluator.Evaluate(Snapshot(unrealized: -300.01m));

        Assert.Equal(RiskState.Locked, assessment!.DesiredState);
    }
}
