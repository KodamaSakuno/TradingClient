using TradingClient.Domain.Primitives;

namespace TradingClient.Application.Risk;

/// <summary>
/// 下单前风控链（§6.4）：规则列表注入、逐条执行、首个拒单短路——新增规则不改调用方。
/// 拒单先写审计再返回失败。
/// </summary>
public sealed class PreTradeRiskChain(IReadOnlyList<IPreTradeRiskCheck> rules, IRiskAuditSink audit)
{
    public async Task<Result> CheckAsync(RiskCheckContext context, CancellationToken ct)
    {
        foreach (var rule in rules)
        {
            var rejection = await rule.CheckAsync(context, ct);
            if (rejection is null)
                continue;

            audit.Record(context, rejection);
            return Result.Failure(new ExchangeError(
                rejection.Code, $"[{rejection.RuleName}] {rejection.Reason}"));
        }
        return Result.Success();
    }

    /// <summary>网关下单成功后调用，只通知实现了 IRiskOrderPlacedHook 的规则。</summary>
    public void NotifyOrderPlaced(RiskCheckContext context)
    {
        foreach (var rule in rules)
            if (rule is IRiskOrderPlacedHook hook)
                hook.OnOrderPlaced(context);
    }
}
