using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Trading;

public sealed record Position(
    Symbol Symbol,
    PositionSide Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal UnrealizedPnl,
    int Leverage,
    MarginMode MarginMode);
