namespace TradingClient.Application.Risk;

/// <summary>
/// 风控拦截审计出口（§6.4：每次拦截写审计日志，含规则名与原因）。
/// 当前实现为 Serilog 版（Avalonia 项目）；PostgreSQL 审计表为后续切片（§9.1），接口先行。
/// </summary>
public interface IRiskAuditSink
{
    void Record(RiskCheckContext context, RiskRejection rejection);

    /// <summary>风控状态迁移审计（§6.4：每次状态变更写审计日志）。</summary>
    void RecordStateTransition(RiskStateTransition transition);

    /// <summary>kill switch 动作审计（§6.4）：每次实际执行的撤单动作，含触发源与成败。</summary>
    void RecordKillSwitch(RiskKillSwitchAction action);
}

/// <summary>kill switch 撤单动作。Trigger：Locked（进入 Locked 态）/ Disconnect（连接断开）。</summary>
public sealed record RiskKillSwitchAction(string Trigger, bool Succeeded, string? ErrorCode, DateTimeOffset Timestamp);
