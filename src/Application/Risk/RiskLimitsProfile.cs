namespace TradingClient.Application.Risk;

public sealed record RiskRuleConfig(
    decimal MaxOrderQuantity,
    decimal MaxDailyQuantity,
    decimal MaxPositionQuantity,
    decimal MaxPriceDeviationRatio,
    double DuplicatePriceToleranceRatio,
    TimeSpan DuplicateWindow);

/// <summary>
/// 事中监控配置（第二层）：阈值均为正数口径的"亏损/敞口金额"，评估器内部取负比较。
/// DayCutOffset 是日切时区偏移——交易所无日切字段，当日已实现盈亏按"本地日切 + 基线差分"近似，
/// 默认 UTC+8 是交易所结算口径的假设，与真实结算日对齐与否影响口径，需注释周知。
/// </summary>
public sealed record RiskMonitorConfig(
    decimal DailyLossWarning,
    decimal DailyLossReduceOnly,
    decimal DailyLossLocked,
    decimal ExposureWarning,
    decimal ExposureReduceOnly,
    bool KillSwitchOnLocked,
    bool KillSwitchOnDisconnect,
    TimeSpan DayCutOffset)
{
    // 演示默认值（真实限额应按账户规模配置）；旧配置文件无 monitor 字段时经 MonitorOrDefault 回落到这里
    public static RiskMonitorConfig Default { get; } = new(
        DailyLossWarning: 100m,
        DailyLossReduceOnly: 200m,
        DailyLossLocked: 300m,
        ExposureWarning: 10_000m,
        ExposureReduceOnly: 20_000m,
        KillSwitchOnLocked: true,
        KillSwitchOnDisconnect: true,
        DayCutOffset: TimeSpan.FromHours(8));
}

/// <summary>
/// 全局默认 + 按 Symbol.Raw 覆盖；覆盖为整组替换（不做字段级合并），保持语义简单。
/// 规则在组装时一次性加载本配置，运行时修改需重启生效。
/// </summary>
public sealed record RiskLimitsProfile(
    RiskRuleConfig Default,
    IReadOnlyDictionary<string, RiskRuleConfig> PerSymbol,
    // null = 沿用 RiskMonitorConfig.Default（旧版 risk-limits.json 无此字段的兼容路径）
    RiskMonitorConfig? Monitor = null)
{
    public RiskRuleConfig For(string symbolRaw) =>
        PerSymbol.TryGetValue(symbolRaw, out var config) ? config : Default;

    public RiskMonitorConfig MonitorOrDefault => Monitor ?? RiskMonitorConfig.Default;
}

public interface IRiskLimitsStore
{
    Task<RiskLimitsProfile?> LoadAsync(CancellationToken ct);

    Task SaveAsync(RiskLimitsProfile profile, CancellationToken ct);
}
