namespace TradingClient.Application.Risk;

public sealed record RiskRejection(string RuleName, string Code, string Reason);

/// <summary>下单前风控规则（§6.4）：可插拔，由 PreTradeRiskChain 逐条执行，返回 null 表示通过。</summary>
public interface IPreTradeRiskCheck
{
    string RuleName { get; }

    ValueTask<RiskRejection?> CheckAsync(RiskCheckContext context, CancellationToken ct);
}

/// <summary>
/// 下单成功后的记账钩子：日累计量等状态型规则实现它，
/// 由用例在网关成功后经 PreTradeRiskChain.NotifyOrderPlaced 通知。
/// </summary>
public interface IRiskOrderPlacedHook
{
    void OnOrderPlaced(RiskCheckContext context);
}
