namespace TradingClient.Exchanges.Bitget.Models;

/// <summary>
/// Bitget V3 响应信封；Code 为 "00000" 表示成功。
/// </summary>
internal sealed record BitgetResponse<T>(
    string Code,
    string Msg,
    long RequestTime,
    T? Data);
