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
}
