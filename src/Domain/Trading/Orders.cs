using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Trading;

public sealed record SpotOrder(
    string OrderId,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    decimal? Price, // null 为市价
    decimal Quantity,
    decimal FilledQuantity,
    OrderStatus Status,
    DateTimeOffset CreatedAt);

public sealed record FuturesOrder(
    string OrderId,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    decimal? Price, // null 为市价
    decimal Quantity,
    decimal FilledQuantity,
    OrderStatus Status,
    PositionSide PositionSide,
    MarginMode MarginMode,
    DateTimeOffset CreatedAt);
