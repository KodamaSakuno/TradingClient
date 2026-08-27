using TradingClient.Domain.Instruments;
using TradingClient.Domain.Options;

namespace TradingClient.Application.UseCases.Options;

/// <summary>
/// 期权链分析（§12）：纯计算、无状态，不接任何交易所。
/// 理论价用 BAW（高频批量定价正是它的定位），Greeks 用 AmericanGreeks bump 法，
/// IV 列对理论价往返反解作引擎自证。任一单侧计算失败只置该单元格 IV 为 null，不炸整链。
/// </summary>
public sealed class OptionChainAnalytics
{
    public IReadOnlyList<OptionQuoteRow> BuildChain(OptionChainRequest request)
    {
        double t = DateConventions.YearFraction(request.ValuationDate, request.Expiry);
        double atmStrike = Math.Round(request.Forward / request.StrikeStep) * request.StrikeStep;
        int half = request.StrikeCount / 2;

        var rows = new List<OptionQuoteRow>(request.StrikeCount);
        for (int i = -half; i <= half; i++)
        {
            double strike = atmStrike + i * request.StrikeStep;
            if (strike <= 0)
                continue; // 档距过大时低端行权价可能越界，跳过该档而非炸面板
            double m = Math.Log(strike / request.Forward);
            rows.Add(BuildRow(request, strike, m, request.Smile.Vol(m), t));
        }
        return rows;
    }

    public OptionPortfolioSummary Summarize(
        IReadOnlyList<OptionPosition> positions, OptionChainRequest market, double futuresMultiplier)
    {
        var rows = new List<PositionGreeksRow>(positions.Count);
        foreach (var p in positions)
        {
            double t = DateConventions.YearFraction(market.ValuationDate, p.Symbol.Expiry);
            double strike = (double)p.Symbol.Strike;
            double sigma = market.Smile.Vol(Math.Log(strike / market.Forward));
            var greeks = AmericanGreeks.Greeks(
                BawApproximation.Price, market.Forward, strike, t, market.Rate, sigma, p.Symbol.Right);
            rows.Add(new PositionGreeksRow(p, Scale(greeks, p.Quantity)));
        }

        var totals = new OptionGreeks(
            rows.Sum(r => r.Greeks.Delta),
            rows.Sum(r => r.Greeks.Gamma),
            rows.Sum(r => r.Greeks.Vega),
            rows.Sum(r => r.Greeks.Theta),
            rows.Sum(r => r.Greeks.Rho));
        return new OptionPortfolioSummary(rows, totals, CreateHedgeAdvice(totals.Delta, futuresMultiplier));
    }

    /// <summary>
    /// 净 Delta（吨）÷ 期货乘数（吨/手）→ 对冲手数，四舍五入取整（MidpointRounding.AwayFromZero）；
    /// 取整后不足一手视为无需对冲。方向与净 Delta 相反：正 → 做空期货，负 → 做多期货。
    /// </summary>
    public static HedgeAdvice CreateHedgeAdvice(double netDelta, double futuresMultiplier)
    {
        int lots = (int)Math.Round(Math.Abs(netDelta) / futuresMultiplier, MidpointRounding.AwayFromZero);
        var direction = lots == 0
            ? HedgeDirection.None
            : netDelta > 0 ? HedgeDirection.ShortFutures : HedgeDirection.LongFutures;
        return new HedgeAdvice(direction, lots, netDelta);
    }

    private static OptionQuoteRow BuildRow(
        OptionChainRequest request, double strike, double logMoneyness, double sigma, double t)
    {
        double f = request.Forward;
        double r = request.Rate;
        double callTheo = BawApproximation.Price(f, strike, t, r, sigma, OptionRight.Call);
        double putTheo = BawApproximation.Price(f, strike, t, r, sigma, OptionRight.Put);
        var callGreeks = AmericanGreeks.Greeks(BawApproximation.Price, f, strike, t, r, sigma, OptionRight.Call);
        var putGreeks = AmericanGreeks.Greeks(BawApproximation.Price, f, strike, t, r, sigma, OptionRight.Put);
        return new OptionQuoteRow(
            strike, logMoneyness, sigma,
            callTheo, putTheo, callGreeks, putGreeks,
            SolveIv(callTheo, f, strike, t, r, OptionRight.Call),
            SolveIv(putTheo, f, strike, t, r, OptionRight.Put));
    }

    private static double? SolveIv(double theo, double f, double strike, double t, double r, OptionRight right)
    {
        var result = ImpliedVolatility.Solve(BawApproximation.Price, theo, f, strike, t, r, right);
        return result.IsSuccess ? result.Value : null;
    }

    private static OptionGreeks Scale(OptionGreeks g, double quantity)
        => new(g.Delta * quantity, g.Gamma * quantity, g.Vega * quantity, g.Theta * quantity, g.Rho * quantity);
}
