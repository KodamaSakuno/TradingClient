using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;

namespace TradingClient.Application.UseCases.Spot;

/// <summary>
/// 现货撤单用例：撤单无对齐/校验维度，直接转发给连接器。
/// </summary>
public sealed class CancelSpotOrder(ISpotTrading trading)
{
    public Task<Result> ExecuteAsync(Symbol symbol, string orderId, CancellationToken ct) =>
        trading.CancelSpotOrderAsync(symbol, orderId, ct);
}
