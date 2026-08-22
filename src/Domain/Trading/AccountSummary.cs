using TradingClient.Domain.Primitives;

namespace TradingClient.Domain.Trading;

/// <summary>
/// 账户模型归一化
/// 普通/统一账户的差异由适配器处理
/// </summary>
public sealed record AccountSummary(
    AccountMode Mode,
    decimal TotalEquity,
    decimal AvailableMargin,
    decimal InitialMargin,
    decimal MaintenanceMargin,
    decimal MarginRatio,
    IReadOnlyList<AssetBalance> Assets);

public sealed record AssetBalance(
    string Asset,
    decimal Total,
    decimal Frozen,
    decimal? CollateralWeight,
    decimal EquityValue);
