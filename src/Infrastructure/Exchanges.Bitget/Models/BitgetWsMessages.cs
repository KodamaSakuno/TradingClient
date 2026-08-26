using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Bitget.Models;

// Bitget UTA WS 公共频道（.local/bitget/uta/websocket/public/）
// 订阅帧：{"op":"subscribe","args":[{"instType":"spot","topic":"ticker","symbol":"BTCUSDT"}]}
internal sealed record BitgetWsRequest(string Op, BitgetWsChannelArg[] Args);

internal sealed record BitgetWsChannelArg(string InstType, string Topic, string Symbol);

/// <summary>
/// 入站信封同时覆盖两种形态：
/// ack：{"event":"subscribe"|"error","arg":{...},"code":"...","msg":"...","connId":"..."}
/// 推送：{"arg":{...},"action":"snapshot"|"update","data":[...],"ts":1736371332162}
/// </summary>
internal sealed record BitgetWsEnvelope(
    string? Event,
    string? Code,
    string? Msg,
    BitgetWsChannelArg? Arg,
    string? Action,
    JsonElement Data,
    long? Ts);

/// <summary>ticker 频道 data 项（数值全字符串）</summary>
internal sealed record BitgetWsTicker(
    string? LastPrice,
    string? Bid1Price,
    string? Bid1Size,
    string? Ask1Price,
    string? Ask1Size);

/// <summary>publicTrade 频道 data 项，字段为压缩单字母名，不能被全局 CamelCase 策略改写</summary>
internal sealed record BitgetWsPublicTrade(
    [property: JsonPropertyName("i")] string TradeId,
    [property: JsonPropertyName("p")] string Price,
    [property: JsonPropertyName("v")] string Quantity,
    [property: JsonPropertyName("S")] string Side,
    [property: JsonPropertyName("T")] string TimestampMs);

/// <summary>
/// books 频道 data 项；seq/pseq 文档表格标 String 但官方示例均为数字（"pseq":0），按数字解析；
/// 快照的 pseq 为 0，增量帧的 pseq 等于上一帧 seq（丢包检测），领域 OrderBookDelta 无序列号字段故不外传
/// </summary>
internal sealed record BitgetWsBook(
    [property: JsonPropertyName("b")] string[][]? Bids,
    [property: JsonPropertyName("a")] string[][]? Asks,
    long Seq,
    long Pseq,
    [property: JsonPropertyName("ts")] string? MatchTimestampMs);
