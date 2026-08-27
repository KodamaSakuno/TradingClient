namespace TradingClient.Application.Risk.Evaluators;

/// <summary>
/// 账户总敞口（notional）上限（§6.4）：分 Warning / ReduceOnly 两档，敞口超限不直接 Locked。
/// 边界约定与事前链 PriceDeviationRule 一致：严格超过阈值才触发，恰好等于阈值不触发。
/// </summary>
public sealed class TotalExposureLimitEvaluator(RiskMonitorConfig config) : IRiskEvaluator
{
    public string EvaluatorName => "TotalExposureLimit";

    public RiskAssessment? Evaluate(RiskSnapshot snapshot) =>
        snapshot.TotalExposure switch
        {
            _ when snapshot.TotalExposure > config.ExposureReduceOnly => Assess(RiskState.ReduceOnly, snapshot.TotalExposure, config.ExposureReduceOnly),
            _ when snapshot.TotalExposure > config.ExposureWarning => Assess(RiskState.Warning, snapshot.TotalExposure, config.ExposureWarning),
            _ => null,
        };

    private RiskAssessment Assess(RiskState state, decimal exposure, decimal threshold) =>
        new(state, EvaluatorName,
            $"Total exposure {exposure} exceeded the {state} threshold {threshold}.");
}
