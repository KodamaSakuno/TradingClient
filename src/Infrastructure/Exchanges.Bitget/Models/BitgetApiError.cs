namespace TradingClient.Exchanges.Bitget.Models;

/// <summary>
/// Bitget 非 2xx 响应的标准错误体：{ "code": "40009", "msg": "..." }
/// code 是稳定错误码（错误码表见 .local/bitget/uta/error-code/restapi.md），msg 面向人
/// </summary>
internal sealed record BitgetApiError(string? Code, string? Msg);
