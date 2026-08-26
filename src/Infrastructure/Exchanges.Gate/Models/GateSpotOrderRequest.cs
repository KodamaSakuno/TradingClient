using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// POST /spot/orders 请求体；Gate 数值字段一律为字符串（精度惯例，勿改 double）。
/// amount 语义随 type/side 变化：limit 为 base 币数量；market buy 为 quote 币金额（本客户端不下此类单，见 GateConnector）；
/// market sell 为 base 币数量。price 仅 limit 单必填。
/// </summary>
internal sealed record GateSpotOrderRequest(
    string CurrencyPair,
    string Type,
    string Side,
    string Amount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Price,
    string TimeInForce);
