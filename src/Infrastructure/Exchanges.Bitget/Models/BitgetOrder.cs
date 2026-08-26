using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Bitget.Models;

// POST /api/v3/trade/place-order 请求体（.local/bitget/catalog/trading-order-management/uta-trade-order.md）
// price 市价单省略；timeInForce 限价单省略时服务端默认 gtc，这里显式给 gtc
internal sealed record BitgetPlaceOrderRequest(
    string Category,
    string Symbol,
    string Side,
    string OrderType,
    string Qty,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Price,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeInForce);

// POST /api/v3/trade/cancel-order 请求体；orderId 优先于 clientOid，不需要 symbol
internal sealed record BitgetCancelOrderRequest(string OrderId, string Category);

/// <summary>place-order / cancel-order 响应的 data：仅订单 ID，不含订单状态</summary>
internal sealed record BitgetOrderAck(string OrderId, string ClientOid);
