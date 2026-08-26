namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// Gate /spot/accounts 单条记录；数值字段以字符串返回（精度惯例，勿改 double）
/// </summary>
internal sealed record GateSpotAccount(
    string Currency,
    string Available,
    string Locked,
    long UpdateId);
