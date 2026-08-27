using TradingClient.Domain.Trading;

namespace TradingClient.Application.Risk.Rules;

/// <summary>
/// 价格偏离保护：仅限价单且 LatestPrice 非 null 时检查，偏离比例超阈值拒。
/// "要求二次确认"是 UI 概念，规则层只做拒绝。
/// </summary>
public sealed class PriceDeviationRule(RiskLimitsProfile profile) : IPreTradeRiskCheck
{
    public string RuleName => "PriceDeviation";

    public ValueTask<RiskRejection?> CheckAsync(RiskCheckContext context, CancellationToken ct)
    {
        if (context is not { Type: OrderType.Limit, Price: { } price, LatestPrice: { } latest }
            || latest <= 0)
            return ValueTask.FromResult<RiskRejection?>(null);

        var limit = profile.For(context.Symbol.Raw).MaxPriceDeviationRatio;
        var deviation = Math.Abs(price - latest) / latest;
        return ValueTask.FromResult(deviation > limit
            ? new RiskRejection(RuleName, "PRICE_DEVIATION_EXCEEDED",
                $"Price {price} deviates from latest {latest} by {deviation:P2}, limit {limit:P2}.")
            : (RiskRejection?)null);
    }
}
