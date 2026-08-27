using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// GET /futures/{settle}/positions 元素（录制形态见 .local/gate_api_futures_p_restful.md）。
/// 数值字段以字符串返回（精度惯例，勿改 double）；size 为带符号张数，文档示例为字符串，双态通吃。
/// leverage 语义陷阱："0"=全仓（实际杠杆上限看 CrossLeverageLimit），非 0=逐仓杠杆。
/// </summary>
internal sealed record GateFuturesPosition(
    string Contract,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long Size,
    string EntryPrice,
    string UnrealisedPnl,
    string Leverage,
    string? CrossLeverageLimit,
    // single / dual_long / dual_short；REST 侧据此映射 PositionSide（WS 侧同款映射见 GateFuturesPositionUpdate）
    string Mode,
    // 该合约生命周期累计已实现盈亏（字符串数值）；无日切字段，日维度只能做基线差分近似
    string? HistoryPnl = null);
