namespace TradingClient.Domain.Options;

/// <summary>
/// 日期换算。ACT/365 固定：国内商品期权实务的近似惯例；交易日历法（剔除节假日）是增强项。
/// </summary>
public static class DateConventions
{
    /// <summary>估值日到到期日的年化剩余期限；已过期（expiry 早于 valuationDate）按 0 处理。</summary>
    public static double YearFraction(DateOnly valuationDate, DateOnly expiry)
        => Math.Max(expiry.DayNumber - valuationDate.DayNumber, 0) / 365.0;
}
