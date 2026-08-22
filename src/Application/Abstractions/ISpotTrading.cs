using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Abstractions;

public interface ISpotTrading : IExchangeConnector
{
    Task<Result<SpotOrder>> PlaceSpotOrderAsync(PlaceSpotOrderRequest req, CancellationToken ct);

    Task<Result> CancelSpotOrderAsync(Symbol symbol, string orderId, CancellationToken ct);

    IObservable<SpotOrderUpdate> SpotOrderUpdates { get; }
}

public sealed record PlaceSpotOrderRequest(
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    decimal? Price,
    decimal Quantity);
