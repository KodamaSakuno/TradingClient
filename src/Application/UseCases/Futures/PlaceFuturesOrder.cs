using TradingClient.Application.Abstractions;
using TradingClient.Application.Risk;
using TradingClient.Application.Services;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.UseCases.Futures;

/// <summary>
/// 合约下单用例：镜像现货——先基于 Instrument 对齐并校验（§4.2），再过下单前风控链（§6.4），
/// 全部通过后转发给连接器。
/// </summary>
public sealed class PlaceFuturesOrder(IFuturesTrading trading, InstrumentCache instruments, PreTradeRiskChain riskChain)
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

        // LatestPrice / CurrentPositionQuantity 暂无快照源，传 null；依赖它们的规则跳过（见 RiskCheckContext）
        var riskContext = new RiskCheckContext(
            trading, req.Symbol, req.Side, req.Type, price, quantity,
            LatestPrice: null, CurrentPositionQuantity: null);
        var riskResult = await riskChain.CheckAsync(riskContext, ct);
        if (!riskResult.IsSuccess)
            return Result.Failure<FuturesOrder>(riskResult.Error!);

        var result = await trading.PlaceFuturesOrderAsync(req with { Price = price, Quantity = quantity }, ct);
        if (result.IsSuccess)
            riskChain.NotifyOrderPlaced(riskContext);
        return result;
    }
}
