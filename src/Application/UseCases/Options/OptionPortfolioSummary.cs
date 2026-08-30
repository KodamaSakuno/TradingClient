using TradingClient.Domain.Options;

namespace TradingClient.Application.UseCases.Options;

public enum HedgeDirection
{
    None,
    LongFutures,
    ShortFutures,
}

/// <summary>
/// Delta 对冲建议。口径：持仓净 Delta 以标的数量（吨）计，÷ 标的期货每手乘数（吨/手）得对冲手数；
/// 对冲方向与净 Delta 相反——净 Delta 为正（组合随标的上涨而盈利）→ 做空期货对冲。
/// </summary>
public sealed record HedgeAdvice(HedgeDirection Direction, int Lots, double NetDelta, double RawLots)
{
    public string Text => Direction switch
    {
        HedgeDirection.ShortFutures => $"净 Delta {NetDelta:F2}（吨）≈ {RawLots:F2} 手 → 按手数取整做空 {Lots} 手标的期货对冲",
        HedgeDirection.LongFutures => $"净 Delta {NetDelta:F2}（吨）≈ {RawLots:F2} 手 → 按手数取整做多 {Lots} 手标的期货对冲",
        _ => $"净 Delta {NetDelta:F2}（吨）→ 折算不足一手，无需对冲",
    };
}

/// <summary>单行持仓 Greeks：引擎每单位 Greeks × 持仓数量（吨）。</summary>
public sealed record PositionGreeksRow(OptionPosition Position, OptionGreeks Greeks);

/// <summary>持仓 Greeks 汇总：逐行 + 合计 + 对冲建议。</summary>
public sealed record OptionPortfolioSummary(
    IReadOnlyList<PositionGreeksRow> Rows,
    OptionGreeks Totals,
    HedgeAdvice Hedge);
