namespace TradingClient.Exchanges.Bitget.Models;

/// <summary>
/// GET /api/v2/public/time 的 data 字段；serverTime 为毫秒字符串。
/// V3 无公共时间接口，校时只能复用 V2（跨版本怪癖，见 BitgetConnector.SyncServerTimeAsync 注释）。
/// </summary>
internal sealed record BitgetServerTime(string ServerTime);
