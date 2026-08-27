namespace TradingClient.Application.Risk;

/// <summary>
/// 事中风险评估器（§6.4）：可插拔，RiskMonitor 逐条执行并取最重态。
/// 返回 null 表示不触发（期望 Normal）。
/// 扩展点：希腊字母限额等做市深层规则是实现本接口的新评估器，不动框架——与 §12 期权模块衔接。
/// </summary>
public interface IRiskEvaluator
{
    string EvaluatorName { get; }

    RiskAssessment? Evaluate(RiskSnapshot snapshot);
}
