using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Trading;

public sealed record Position(
    Symbol Symbol,
    PositionSide Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal UnrealizedPnl,
    int Leverage,
    MarginMode MarginMode,
    // 该合约生命周期累计已实现盈亏（Gate history_pnl 口径）；交易所不提供时适配器留 null，
    // 消费方（§6.4 事中监控的当日已实现基线差分）必须容忍 null
    decimal? RealizedPnl = null);
