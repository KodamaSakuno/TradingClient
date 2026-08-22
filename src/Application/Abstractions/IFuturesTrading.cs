using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Abstractions;

public interface IFuturesTrading : IExchangeConnector
{
    Task<Result<FuturesOrder>> PlaceFuturesOrderAsync(PlaceFuturesOrderRequest req, CancellationToken ct);

    Task<Result> SetLeverageAsync(Symbol symbol, int leverage, MarginMode mode, CancellationToken ct);

    Task<Result<IReadOnlyList<Position>>> GetPositionsAsync(CancellationToken ct);

    IObservable<PositionUpdate> PositionUpdates { get; }

    IObservable<LiquidationWarning> LiquidationWarnings { get; }
}

public sealed record PlaceFuturesOrderRequest(
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    decimal? Price,
    decimal Quantity,
    PositionSide PositionSide,
    MarginMode MarginMode,
    int? Leverage);
