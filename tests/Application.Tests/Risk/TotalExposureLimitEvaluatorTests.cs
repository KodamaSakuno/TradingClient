using TradingClient.Application.Risk;
using TradingClient.Application.Risk.Evaluators;

namespace TradingClient.Application.Tests.Risk;

public class TotalExposureLimitEvaluatorTests
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

    private readonly TotalExposureLimitEvaluator _evaluator = new(Config);

    private static RiskSnapshot Snapshot(decimal exposure) =>
        new([], new Dictionary<string, decimal>(), 0m, 0m, exposure, DateTimeOffset.UtcNow);

    [Fact]
    public void Evaluate_ExposureBelowWarning_DoesNotTrigger()
    {
        Assert.Null(_evaluator.Evaluate(Snapshot(999.99m)));
    }

    [Fact]
    public void Evaluate_ExposureExactlyAtWarningThreshold_DoesNotTrigger()
    {
        // 边界约定与 PriceDeviationRule 一致：严格超过才触发，恰好等于不触发
        Assert.Null(_evaluator.Evaluate(Snapshot(1_000m)));
    }

    [Fact]
    public void Evaluate_ExposureBeyondWarningThreshold_RequestsWarning()
    {
        var assessment = _evaluator.Evaluate(Snapshot(1_000.01m));

        Assert.NotNull(assessment);
        Assert.Equal(RiskState.Warning, assessment.DesiredState);
        Assert.Equal("TotalExposureLimit", assessment.EvaluatorName);
    }

    [Fact]
    public void Evaluate_ExposureExactlyAtReduceOnlyThreshold_RequestsWarningOnly()
    {
        var assessment = _evaluator.Evaluate(Snapshot(2_000m));

        Assert.Equal(RiskState.Warning, assessment!.DesiredState);
    }

    [Fact]
    public void Evaluate_ExposureBeyondReduceOnlyThreshold_RequestsReduceOnly()
    {
        var assessment = _evaluator.Evaluate(Snapshot(2_000.01m));

        Assert.Equal(RiskState.ReduceOnly, assessment!.DesiredState);
    }
}
