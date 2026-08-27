using Serilog;
using TradingClient.Application.Risk;

namespace TradingClient.Avalonia.Risk;

/// <summary>
/// 风控拦截审计的 Serilog 实现（§6.4：每次拦截写审计日志，含规则名与原因）。
/// PostgreSQL 审计表为后续切片（§9.1）。
/// </summary>
public sealed class SerilogRiskAuditSink(ILogger logger) : IRiskAuditSink
{
    public void Record(RiskCheckContext context, RiskRejection rejection) =>
        logger.Warning(
            "Risk rejection: {RuleName} {Code} {Reason} Symbol={Symbol} Side={Side} Type={OrderType} Price={Price} Quantity={Quantity}",
            rejection.RuleName, rejection.Code, rejection.Reason,
            context.Symbol.Raw, context.Side, context.Type, context.Price, context.Quantity);

    public void RecordStateTransition(RiskStateTransition transition) =>
        logger.Warning(
            "Risk state transition: {From} -> {To} Reason={Reason} Timestamp={Timestamp}",
            transition.From, transition.To, transition.Reason, transition.Timestamp);
}
