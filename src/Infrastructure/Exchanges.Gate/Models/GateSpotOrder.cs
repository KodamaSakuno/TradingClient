namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// Gate 现货订单对象（POST/DELETE /spot/orders 响应，录制形态见 .local/gate_api_spot_restful.txt）。
/// 数值字段以字符串返回（精度惯例，勿改 double）；Left 为未成交数量，已成交量 = Amount - Left。
/// </summary>
internal sealed record GateSpotOrder(
    string Id,
    string Status,
    string CurrencyPair,
    string Type,
    string Side,
    string Amount,
    string? Price,
    string Left,
    long CreateTimeMs);
