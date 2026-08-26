namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// Gate /spot/time，server_time 为毫秒级 Unix 时间戳。
/// </summary>
internal sealed record GateServerTime(long ServerTime);
