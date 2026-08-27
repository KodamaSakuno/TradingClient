using TradingClient.Application.Risk;
using TradingClient.Domain.Instruments;

namespace TradingClient.Application.Tests.Fakes;

/// <summary>可编程的风控快照源：按 Symbol.Raw 返回预设值，未设置的 Symbol 返回 null（规则跳过路径）。</summary>
public sealed class FakeRiskSnapshotSource : IRiskSnapshotSource
{
    private readonly Dictionary<string, decimal> _latestPrices = new();
    private readonly Dictionary<string, decimal> _positionQuantities = new();

    public void SetLatestPrice(Symbol symbol, decimal price) => _latestPrices[symbol.Raw] = price;

    public void SetPositionQuantity(Symbol symbol, decimal quantity) => _positionQuantities[symbol.Raw] = quantity;

    public decimal? GetLatestPrice(Symbol symbol) =>
        _latestPrices.TryGetValue(symbol.Raw, out var price) ? price : null;

    public decimal? GetCurrentPositionQuantity(Symbol symbol) =>
        _positionQuantities.TryGetValue(symbol.Raw, out var quantity) ? quantity : null;
}
