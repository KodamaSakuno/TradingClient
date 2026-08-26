namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// Gate 非 2xx 响应的标准错误体：{ "label": "...", "message": "..." }
/// label 是稳定错误码（如 INVALID_KEY），message 面向人
/// </summary>
internal sealed record GateApiError(string? Label, string? Message);
