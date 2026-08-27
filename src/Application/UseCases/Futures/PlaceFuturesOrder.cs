using TradingClient.Application.Abstractions;
using TradingClient.Application.Risk;
using TradingClient.Application.Services;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.UseCases.Futures;

/// <summary>
/// 合约下单用例：镜像现货——先基于 Instrument 对齐并校验，再过下单前风控链，
/// 全部通过后转发给连接器。
/// </summary>
public sealed class PlaceFuturesOrder(IFuturesTrading trading, InstrumentCache instruments, PreTradeRiskChain riskChain, IRiskSnapshotSource snapshots)
{
    public async Task<Result<FuturesOrder>> ExecuteAsync(PlaceFuturesOrderRequest req, CancellationToken ct)
    {
        var instrument = await instruments.GetAsync(req.Symbol, ct);
        if (instrument is null)
            return Result.Failure<FuturesOrder>(new ExchangeError(
                "UNKNOWN_INSTRUMENT", $"Unknown instrument: {req.Symbol.Raw}"));

        var price = req.Price is { } p ? instrument.AlignPrice(p) : (decimal?)null;
        var quantity = instrument.AlignQuantity(req.Quantity);

        // Floor 对齐后可能落到 0 或低于 MinQuantity，由 ValidateOrder 捕获
        var validation = instrument.ValidateOrder(price, quantity);
        if (!validation.IsSuccess)
            return Result.Failure<FuturesOrder>(validation.Error!);

        // 最新价 / 带符号净持仓取自快照源（RiskMonitor 内部表）；该 Symbol 无行情订阅或无持仓时为 null，
        // 依赖它们的规则（PriceDeviation/PositionLimit）跳过
        var riskContext = new RiskCheckContext(
            trading, req.Symbol, req.Side, req.Type, price, quantity,
            snapshots.GetLatestPrice(req.Symbol), snapshots.GetCurrentPositionQuantity(req.Symbol));
        var riskResult = await riskChain.CheckAsync(riskContext, ct);
        if (!riskResult.IsSuccess)
            return Result.Failure<FuturesOrder>(riskResult.Error!);

        var result = await trading.PlaceFuturesOrderAsync(req with { Price = price, Quantity = quantity }, ct);
        if (result.IsSuccess)
            riskChain.NotifyOrderPlaced(riskContext);
        return result;
    }
}
