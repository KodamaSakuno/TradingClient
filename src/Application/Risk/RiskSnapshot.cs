using TradingClient.Domain.Trading;

namespace TradingClient.Application.Risk;

/// <summary>
/// 事中风险快照：由 RiskMonitor 在每次持仓/行情更新后重建，喂给各 IRiskEvaluator。
/// 口径（均为近似，不假装精确）：
/// - DailyRealizedPnl：history_pnl 是合约生命周期累计已实现盈亏，交易所无日切字段，
///   日维度只能做基线差分近似（日切时重置基线；监控启动前/新 Symbol 出现前的当日已实现不可知）。
/// - TotalUnrealizedPnl：(最新价 − 开仓价) × 数量 × 方向 的本地估算，非交易所口径。
/// - TotalExposure：Σ |数量| × 最新价；无最新价的 Symbol 退化为用开仓价估值。
/// </summary>
public sealed record RiskSnapshot(
    IReadOnlyList<Position> Positions,
    IReadOnlyDictionary<string, decimal> LatestPrices,
    decimal DailyRealizedPnl,
    decimal TotalUnrealizedPnl,
    decimal TotalExposure,
    DateTimeOffset Timestamp);

public sealed record RiskAssessment(RiskState DesiredState, string EvaluatorName, string Reason);
