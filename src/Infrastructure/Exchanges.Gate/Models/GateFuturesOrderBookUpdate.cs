using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// futures.order_book_update 推送（result 为单对象，档位是 {p,s} 对象而非现货的数组）
/// 字段名对齐 .local/gate_api_futures_p_ws.md 的 order book update notification
/// </summary>
internal sealed record GateFuturesOrderBookUpdate(
    [property: JsonPropertyName("t")] long UpdateTimeMs,
    // true=全量快照（整体替换本地盘口）；false 时字段不出现
    [property: JsonPropertyName("full")] bool? Full,
    [property: JsonPropertyName("s")] string Contract,
    // U/u 为首末 update_id；领域 OrderBookDelta 无序列号字段，不外传（乱序检测与重订未实现）
    [property: JsonPropertyName("U")] long FirstUpdateId,
    [property: JsonPropertyName("u")] long LastUpdateId,
    [property: JsonPropertyName("b")] GateFuturesOrderBookLevel[]? Bids,
    [property: JsonPropertyName("a")] GateFuturesOrderBookLevel[]? Asks);

/// <summary>
/// 期货盘口档位；s 是该价位变更后的绝对量（单位张），0 表示删除该价位
/// </summary>
internal sealed record GateFuturesOrderBookLevel(
    [property: JsonPropertyName("p")] string Price,
    // 不带 X-Gate-Size-Decimal 头为整数，字符串与裸数字两种形态都在文档出现
    [property: JsonPropertyName("s")]
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    decimal Size);
