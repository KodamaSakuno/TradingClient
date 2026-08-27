using TradingClient.Application.Abstractions;
using TradingClient.Application.Risk;
using TradingClient.Application.Services;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.UseCases.Spot;

/// <summary>
/// 现货下单用例：先基于 Instrument 对齐并校验，再过下单前风控链，
/// 全部通过后转发给连接器。校验在适配器之外先做一遍，作为面向 UI 的第一道防线。
/// </summary>
public sealed class PlaceSpotOrder(ISpotTrading trading, InstrumentCache instruments, PreTradeRiskChain riskChain, IRiskSnapshotSource snapshots)
{
    public async Task<Result<SpotOrder>> ExecuteAsync(PlaceSpotOrderRequest req, CancellationToken ct)
    {
        var instrument = await instruments.GetAsync(req.Symbol, ct);
        if (instrument is null)
            return Result.Failure<SpotOrder>(new ExchangeError(
                "UNKNOWN_INSTRUMENT", $"Unknown instrument: {req.Symbol.Raw}"));

        var price = req.Price is { } p ? instrument.AlignPrice(p) : (decimal?)null;
        var quantity = instrument.AlignQuantity(req.Quantity);

        // Floor 对齐后可能落到 0 或低于 MinQuantity，由 ValidateOrder 捕获
        var validation = instrument.ValidateOrder(price, quantity);
        if (!validation.IsSuccess)
            return Result.Failure<SpotOrder>(validation.Error!);

        // 快照源（RiskMonitor）只跟踪合约：现货 Symbol 两处查询恒为 null，
        // 依赖它们的规则（PriceDeviation/PositionLimit）跳过——与接入快照源前的行为一致
        var riskContext = new RiskCheckContext(
            trading, req.Symbol, req.Side, req.Type, price, quantity,
            snapshots.GetLatestPrice(req.Symbol), snapshots.GetCurrentPositionQuantity(req.Symbol));
        var riskResult = await riskChain.CheckAsync(riskContext, ct);
        if (!riskResult.IsSuccess)
            return Result.Failure<SpotOrder>(riskResult.Error!);

        var result = await trading.PlaceSpotOrderAsync(req with { Price = price, Quantity = quantity }, ct);
        if (result.IsSuccess)
            riskChain.NotifyOrderPlaced(riskContext);
        return result;
    }
}
