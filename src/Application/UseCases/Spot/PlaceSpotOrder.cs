using TradingClient.Application.Abstractions;
using TradingClient.Application.Services;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.UseCases.Spot;

/// <summary>
/// 现货下单用例：先基于 Instrument 对齐并校验（§4.2），通过后转发给连接器。
/// 校验在适配器之外先做一遍，作为面向 UI 的第一道防线。
/// </summary>
public sealed class PlaceSpotOrder(ISpotTrading trading, InstrumentCache instruments)
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

        return await trading.PlaceSpotOrderAsync(req with { Price = price, Quantity = quantity }, ct);
    }
}
