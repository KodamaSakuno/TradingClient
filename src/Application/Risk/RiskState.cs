namespace TradingClient.Application.Risk;

/// <summary>
/// 风控状态机状态（§6.4）：由事中 IRiskMonitor 写、事前链的 RiskStateGateRule 读。
/// Warning 只告警不拦单；ReduceOnly 仅允许减仓；Locked 全部拒单。
/// </summary>
public enum RiskState
{
    Normal,
    Warning,
    ReduceOnly,
    Locked,
}

public sealed record RiskStateTransition(RiskState From, RiskState To, string Reason, DateTimeOffset Timestamp);
