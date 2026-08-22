using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Trading;

public sealed record Quote(
    Symbol Symbol,
    decimal BestBid,
    decimal BestAsk,
    DateTimeOffset Timestamp);

public sealed record Trade(
    string TradeId,
    Symbol Symbol,
    decimal Price,
    decimal Quantity,
    OrderSide Side,
    DateTimeOffset Timestamp);

public readonly record struct OrderBookLevel(decimal Price, decimal Quantity);

public sealed record OrderBookDelta(
    Symbol Symbol,
    IReadOnlyList<OrderBookLevel> Bids,
    IReadOnlyList<OrderBookLevel> Asks,
    bool IsSnapshot,
    DateTimeOffset Timestamp);

public sealed record Candle(
    Symbol Symbol,
    TimeFrame TimeFrame,
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);
