namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// POST /futures/{settle}/countdown_cancel_all 请求体（死 man's switch，形态见 .local/gate_api_futures_p_restful.md）。
/// timeout 单位秒，至少 5，0 表示关闭倒计时；contract 缺省 = 全部合约。
/// </summary>
internal sealed record GateFuturesCountdownCancelAllRequest(int Timeout, string? Contract = null);
