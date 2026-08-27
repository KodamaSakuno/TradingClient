using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Abstractions;

public interface IFuturesTrading : IExchangeConnector
{
    Task<Result<FuturesOrder>> PlaceFuturesOrderAsync(PlaceFuturesOrderRequest req, CancellationToken ct);

    Task<Result> SetLeverageAsync(Symbol symbol, int leverage, MarginMode mode, CancellationToken ct);

    Task<Result> SetPositionModeAsync(PositionMode mode, CancellationToken ct);

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
    int? Leverage,
    // 自成交防护走交易所侧（§6.4）；null 表示不携带，由适配器决定是否序列化
    SelfTradePrevention? Stp = null);
