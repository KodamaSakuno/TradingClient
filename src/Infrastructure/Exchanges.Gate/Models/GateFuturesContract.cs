using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// Gate GET /futures/{settle}/contracts（返回裸数组，无信封）
/// 字段名对齐 .local/gate_api_futures_p_restful.md
/// </summary>
internal sealed record GateFuturesContract(
    string Name,
    // 1 张 = 多少标的币（如 BTC_USDT 为 "0.0001"），文档与 testnet 均为字符串
    string QuantoMultiplier,
    // 显式 tick（如 "0.1"），不是精度位数，禁止走 Pow10Negative
    string OrderPriceRound,
    // 张数：文档示例为字符串，testnet 实测为裸数字（同 WS code 双态教训），故 AllowReadingFromString + decimal 通吃两种形态
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    decimal OrderSizeMin,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    decimal OrderSizeMax,
    // false 时张数只能为整数；true 允许小数张（精度文档未给出，映射侧保守按 1 张步长，见 GateConnector）
    bool EnableDecimal,
    // prelaunch / trading / delisting / delisted / circuit_breaker
    string Status,
    // 下架过渡期或已下架，true 时一律视为 Suspended
    bool InDelisting);
