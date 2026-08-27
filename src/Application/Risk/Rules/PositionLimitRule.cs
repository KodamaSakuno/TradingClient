using TradingClient.Domain.Trading;

namespace TradingClient.Application.Risk.Rules;

/// <summary>
/// 仓位上限：CurrentPositionQuantity 为 null（无快照源）时跳过；
/// 按方向推算成交后仓位（Buy 加 / Sell 减），绝对值超限拒。
/// </summary>
public sealed class PositionLimitRule(RiskLimitsProfile profile) : IPreTradeRiskCheck
{
    public string RuleName => "PositionLimit";

    public ValueTask<RiskRejection?> CheckAsync(RiskCheckContext context, CancellationToken ct)
    {
        if (context.CurrentPositionQuantity is not { } current)
            return ValueTask.FromResult<RiskRejection?>(null);

        var limit = profile.For(context.Symbol.Raw).MaxPositionQuantity;
        var projected = current + (context.Side == OrderSide.Buy
            ? context.Quantity
            : -context.Quantity);
        return ValueTask.FromResult(Math.Abs(projected) > limit
            ? new RiskRejection(RuleName, "POSITION_LIMIT_EXCEEDED",
                $"Projected position {projected} would exceed limit {limit} for {context.Symbol.Raw}.")
            : (RiskRejection?)null);
    }
}
