using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// futures.positions 私有推送元素（形态见 .local/gate_api_futures_p_ws.md 通知示例）。
/// 注意与 REST 的 GateFuturesPosition 是两个 DTO，字段集不同：推送里数值字段是 JSON number（非字符串），
/// 且无 unrealised_pnl。size 为带符号张数（String/Integer 双态，正=多、负=空）；leverage 语义同 REST：0=全仓。
/// liq_price 已 Deprecated（示例返回 0.1 垃圾值），刻意不收进 DTO。
/// </summary>
internal sealed record GateFuturesPositionUpdate(
    string Contract,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long Size,
    decimal EntryPrice,
    decimal Margin,
    decimal MaintenanceRate,
    decimal CrossLeverageLimit,
    long Leverage,
    // single / dual_long / dual_short
    string Mode,
    // cross / isolated；推送可能缺省，缺失时按 leverage==0 回退全仓
    string? PosMarginMode,
    decimal? UnrealisedPnl,
    // 该合约生命周期累计已实现盈亏（JSON number）；无日切字段，日维度只能做基线差分近似（§6.4）。
    // 推送里没有 mark_price/unrealised_pnl 的实时口径——这是 RiskMonitor 浮动盈亏只能本地估算的根据
    decimal? HistoryPnl = null,
    long? TimeMs = null);
