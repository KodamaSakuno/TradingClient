namespace TradingClient.Application.Risk.Evaluators;

/// <summary>
/// 当日亏损熔断（§6.4）：当日已实现 + 浮动亏损合计分 Warning / ReduceOnly / Locked 三档。
/// 浮动盈亏是快照里的本地估算（entry vs 最新价），口径见 RiskSnapshot 注释。
/// 边界约定与事前链 PriceDeviationRule 一致：严格超过阈值才触发，恰好等于阈值不触发。
/// </summary>
public sealed class DailyLossCircuitBreaker(RiskMonitorConfig config) : IRiskEvaluator
{
    public string EvaluatorName => "DailyLossCircuitBreaker";

    public RiskAssessment? Evaluate(RiskSnapshot snapshot)
    {
        var totalPnl = snapshot.DailyRealizedPnl + snapshot.TotalUnrealizedPnl;
        return totalPnl switch
        {
            _ when totalPnl < -config.DailyLossLocked => Assess(RiskState.Locked, totalPnl, config.DailyLossLocked),
            _ when totalPnl < -config.DailyLossReduceOnly => Assess(RiskState.ReduceOnly, totalPnl, config.DailyLossReduceOnly),
            _ when totalPnl < -config.DailyLossWarning => Assess(RiskState.Warning, totalPnl, config.DailyLossWarning),
            _ => null,
        };
    }

    private RiskAssessment Assess(RiskState state, decimal totalPnl, decimal threshold) =>
        new(state, EvaluatorName,
            $"Daily PnL {totalPnl} exceeded the {state} loss threshold {threshold}.");
}
