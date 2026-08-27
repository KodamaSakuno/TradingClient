using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// futures.trades 推送条目（result 为数组）
/// 字段名对齐 .local/gate_api_futures_p_ws.md 的 trades notification
/// </summary>
internal sealed record GateFuturesTrade(
    long Id,
    string Contract,
    // size 带符号：正=主动买，负=主动卖；单位张
    // 不带 X-Gate-Size-Decimal 头时整数张（小数张向零截断），字符串与裸数字两种形态都在文档出现，故通吃
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    decimal Size,
    string Price,
    long? CreateTime,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    long? CreateTimeMs);
