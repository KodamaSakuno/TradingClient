using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Trading;

public sealed record SpotOrderUpdate(SpotOrder Order, DateTimeOffset Timestamp);

public sealed record PositionUpdate(Position Position, DateTimeOffset Timestamp);

public sealed record LiquidationWarning(
    Symbol Symbol,
    PositionSide Side,
    decimal EstimatedLiquidationPrice,
    decimal MarginRatio,
    DateTimeOffset Timestamp);
