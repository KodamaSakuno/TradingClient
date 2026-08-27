using TradingClient.Domain.Instruments;

namespace TradingClient.Application.UseCases.Options;

/// <summary>
/// 期权实验室演示数据（全本地 mock，不接交易所）。豆粕（m）风格：
/// 标的 3500 元/吨、行权价档距 50、平值 IV 20%、利率 2%、期货乘数 10 吨/手。
/// </summary>
public static class OptionLabDemo
{
    public const double Forward = 3500;
    public const double StrikeStep = 50;
    public const double AtmVol = 0.20;
    public const double SmileSkew = -0.05;
    public const double SmileCurvature = 0.8;
    public const double Rate = 0.02;
    public const double FuturesMultiplier = 10;
    public const int StrikeCount = 11;

    /// <summary>mock 持仓（数量单位：吨，正 = long / 负 = short）；到期日跟随 UI 所选期限。</summary>
    public static IReadOnlyList<OptionPosition> Positions(DateOnly expiry) =>
    [
        new(new OptionSymbol("M", expiry, 3400m, OptionRight.Call), 30, 148.5),
        new(new OptionSymbol("M", expiry, 3500m, OptionRight.Call), -20, 102.0),
        new(new OptionSymbol("M", expiry, 3600m, OptionRight.Put), 25, 95.0),
    ];
}
