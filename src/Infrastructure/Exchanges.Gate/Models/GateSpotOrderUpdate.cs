namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// spot.orders 私有频道推送的订单对象（.local/gate_api_spot_ws.txt 1484 行起）。
/// 数值字段以字符串返回（精度惯例，勿改 double）；Left 为未成交数量，已成交量 = Amount - Left。
/// 状态语义与 REST 不同：由 Event（put/update/finish）+ FinishAs 表达，无 status 字段。
/// </summary>
internal sealed record GateSpotOrderUpdate(
    string Id,
    string CurrencyPair,
    string Type,
    string Side,
    string Amount,
    string? Price,
    string Left,
    string Event,
    string? FinishAs,
    string? CreateTimeMs,
    string? UpdateTimeMs);
