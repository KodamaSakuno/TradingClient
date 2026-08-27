using TradingClient.Domain.Trading;

namespace TradingClient.Application.Risk.Rules;

/// <summary>
/// 重复下单防护：同一 Symbol+Side、价格差在容差比例内、时间落在窗口内的重复提交拒。
/// 通过检查即记录本次提交；窗口外的记录自动过期。
/// </summary>
public sealed class DuplicateOrderRule(RiskLimitsProfile profile, TimeProvider clock) : IPreTradeRiskCheck
{
    private readonly List<RecentOrder> _recent = [];
    private readonly object _gate = new();

    public string RuleName => "DuplicateOrder";

    public ValueTask<RiskRejection?> CheckAsync(RiskCheckContext context, CancellationToken ct)
    {
        var config = profile.For(context.Symbol.Raw);
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            _recent.RemoveAll(r => now - r.Timestamp > config.DuplicateWindow);

            var duplicate = _recent.Any(r =>
                r.SymbolRaw == context.Symbol.Raw
                && r.Side == context.Side
                && PriceMatches(r.Price, context.Price, config.DuplicatePriceToleranceRatio));
            if (duplicate)
                return ValueTask.FromResult<RiskRejection?>(new(RuleName, "DUPLICATE_ORDER",
                    $"Duplicate submission for {context.Symbol.Raw} {context.Side} within {config.DuplicateWindow}."));

            _recent.Add(new RecentOrder(context.Symbol.Raw, context.Side, context.Price, now));
            return ValueTask.FromResult<RiskRejection?>(null);
        }
    }

    // 市价单（Price 为 null）视为价格相同；一侧有一侧无不算重复
    private static bool PriceMatches(decimal? recorded, decimal? incoming, double toleranceRatio) =>
        (recorded, incoming) switch
        {
            (null, null) => true,
            ({ } a, { } b) => (double)Math.Abs(a - b) <= (double)a * toleranceRatio,
            _ => false,
        };

    private sealed record RecentOrder(string SymbolRaw, OrderSide Side, decimal? Price, DateTimeOffset Timestamp);
}
