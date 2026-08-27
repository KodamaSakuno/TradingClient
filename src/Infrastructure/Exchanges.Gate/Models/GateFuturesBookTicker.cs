using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// futures.book_ticker 推送（result 为单对象，BBO 最优买卖一档）
/// 字段名对齐 .local/gate_api_futures_p_ws.md 的 best ask/bid notification
/// </summary>
internal sealed record GateFuturesBookTicker(
    [property: JsonPropertyName("t")] long UpdateTimeMs,
    [property: JsonPropertyName("s")] string Contract,
    // 无买/卖挂单时为对应侧空字符串
    [property: JsonPropertyName("b")] string? BidPrice,
    [property: JsonPropertyName("a")] string? AskPrice);
