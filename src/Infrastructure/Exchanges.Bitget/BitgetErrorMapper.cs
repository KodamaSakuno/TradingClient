using System.Net.Http.Json;
using System.Text.Json;
using TradingClient.Domain.Primitives;
using TradingClient.Exchanges.Bitget.Models;

namespace TradingClient.Exchanges.Bitget;

/// <summary>
/// Bitget 非 2xx 响应 → ExchangeError 的唯一映射点（body code → Code），下单等后续接口复用。
/// 错误体不是 Bitget 标准 JSON 时降级为 HTTP 状态码。
/// </summary>
internal static class BitgetErrorMapper
{
    public static async Task<ExchangeError> FromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        BitgetApiError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync(BitgetJsonContext.Default.BitgetApiError, ct);
        }
        catch (JsonException)
        {
            // 错误体非 JSON（如网关 502 返回 HTML），走状态码降级
        }

        if (error?.Code is { Length: > 0 } code)
            return new ExchangeError(code, error.Msg ?? response.ReasonPhrase ?? string.Empty);

        return new ExchangeError(
            $"HTTP_{(int)response.StatusCode}",
            response.ReasonPhrase ?? "Bitget API request failed.");
    }
}
