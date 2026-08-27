namespace TradingClient.Application.Risk.Rules;

/// <summary>单笔下单量上限（§6.4）。</summary>
public sealed class OrderSizeLimitRule(RiskLimitsProfile profile) : IPreTradeRiskCheck
{
    public string RuleName => "OrderSizeLimit";

    public ValueTask<RiskRejection?> CheckAsync(RiskCheckContext context, CancellationToken ct)
    {
        var limit = profile.For(context.Symbol.Raw).MaxOrderQuantity;
        return ValueTask.FromResult(context.Quantity > limit
            ? new RiskRejection(RuleName, "ORDER_SIZE_EXCEEDED",
                $"Quantity {context.Quantity} exceeds single-order limit {limit} for {context.Symbol.Raw}.")
            : (RiskRejection?)null);
    }
}
