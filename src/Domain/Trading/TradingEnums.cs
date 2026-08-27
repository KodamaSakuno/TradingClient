namespace TradingClient.Domain.Trading;

public enum TimeFrame
{
    M1,
    M5,
    M15,
    H1,
    H4,
    D1,
    W1,
}

public enum OrderSide
{
    Buy,
    Sell,
}

public enum OrderType
{
    Limit,
    Market,
}

public enum OrderStatus
{
    New,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
}

/// <summary>PortfolioMargin 为预留枚举值，暂不实现</summary>
public enum MarginMode
{
    Cross,
    Isolated,
    PortfolioMargin,
}

/// <summary>
/// 自成交防护策略（走交易所侧参数，客户端不做自成交拦截）。
/// 分别对应 Gate stp_act 的 cn / co / cb。
/// </summary>
public enum SelfTradePrevention
{
    CancelNewest,
    CancelOldest,
    CancelBoth,
}

/// <summary>持仓方向；Both 用于单向持仓模式，由适配器负责映射</summary>
public enum PositionSide
{
    Long,
    Short,
    Both,
}

/// <summary>持仓模式；Gate 的 dual_plus（split position）预留不实现</summary>
public enum PositionMode
{
    Single,
    Dual,
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
}
