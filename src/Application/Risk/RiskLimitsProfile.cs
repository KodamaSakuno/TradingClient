namespace TradingClient.Application.Risk;

public sealed record RiskRuleConfig(
    decimal MaxOrderQuantity,
    decimal MaxDailyQuantity,
    decimal MaxPositionQuantity,
    decimal MaxPriceDeviationRatio,
    double DuplicatePriceToleranceRatio,
    TimeSpan DuplicateWindow);

/// <summary>
/// 全局默认 + 按 Symbol.Raw 覆盖；覆盖为整组替换（不做字段级合并），保持语义简单。
/// 规则在组装时一次性加载本配置，运行时修改需重启生效。
/// </summary>
public sealed record RiskLimitsProfile(
    RiskRuleConfig Default,
    IReadOnlyDictionary<string, RiskRuleConfig> PerSymbol)
{
    public RiskRuleConfig For(string symbolRaw) =>
        PerSymbol.TryGetValue(symbolRaw, out var config) ? config : Default;
}

public interface IRiskLimitsStore
{
    Task<RiskLimitsProfile?> LoadAsync(CancellationToken ct);

    Task SaveAsync(RiskLimitsProfile profile, CancellationToken ct);
}
