using System.Net.Http.Json;
using System.Text.Json;
using TradingClient.Domain.Primitives;
using TradingClient.Exchanges.Gate.Models;

namespace TradingClient.Exchanges.Gate;

/// <summary>
/// Gate 非 2xx 响应 → ExchangeError 的唯一映射点（label → Code），下单等后续接口复用。
/// 错误体不是 Gate 标准 JSON 时降级为 HTTP 状态码。
/// </summary>
internal static class GateErrorMapper
{
    public static async Task<ExchangeError> FromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        GateApiError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync(GateJsonContext.Default.GateApiError, ct);
        }
        catch (JsonException)
        {
            // 错误体非 JSON（如网关 502 返回 HTML），走状态码降级
        }

        if (error?.Label is { Length: > 0 } label)
            return new ExchangeError(label, error.Message ?? response.ReasonPhrase ?? string.Empty);

        return new ExchangeError(
            $"HTTP_{(int)response.StatusCode}",
            response.ReasonPhrase ?? "Gate API request failed.");
    }
}
