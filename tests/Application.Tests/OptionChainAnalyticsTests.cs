using TradingClient.Application.UseCases.Options;
using TradingClient.Domain.Instruments;

namespace TradingClient.Application.Tests;

public class OptionChainAnalyticsTests
{
    private static readonly DateOnly Valuation = new(2026, 1, 1);
    private static readonly DateOnly Expiry = new(2026, 4, 1);

    // 豆粕风格演示参数：F=3500、档距 50、平值 σ=20%、r=2%、期货乘数 10 吨/手
    private static OptionChainRequest DemoRequest(SmileParameters? smile = null) =>
        new(3500, 0.02, smile ?? new SmileParameters(0.20, -0.05, 0.8),
            Valuation, Expiry, StrikeCount: 11, StrikeStep: 50);

    [Fact]
    public void Vol_ZeroMoneyness_ReturnsAtmVol()
    {
        var smile = new SmileParameters(0.20, -0.05, 0.8);

        // m=0 处偏斜/曲率项均为零，σ = σ₀
        Assert.Equal(0.20, smile.Vol(0), 12);
        // 二次偏斜口径：σ(m) = σ₀ + a·m + b·m²
        const double m = 0.07;
        Assert.Equal(0.20 - 0.05 * m + 0.8 * m * m, smile.Vol(m), 12);
    }

    [Fact]
    public void BuildChain_DemoParameters_EveryStrikeTheoPositiveAndIvRoundTripsInputVol()
    {
        var analytics = new OptionChainAnalytics();

        var rows = analytics.BuildChain(DemoRequest());

        Assert.Equal(11, rows.Count);
        foreach (var row in rows)
        {
            Assert.True(row.CallTheo > 0);
            Assert.True(row.PutTheo > 0);
            // IV 往返 = 引擎自证：对 BAW 理论价反解应 ≈ 微笑模型输入 σ(m)，容差 1e-3
            Assert.NotNull(row.CallIv);
            Assert.NotNull(row.PutIv);
            Assert.Equal(row.InputVol, row.CallIv!.Value, 3);
            Assert.Equal(row.InputVol, row.PutIv!.Value, 3);
        }
    }

    [Fact]
    public void BuildChain_DemoParameters_InputVolFollowsSmileModel()
    {
        var analytics = new OptionChainAnalytics();
        var smile = new SmileParameters(0.20, -0.05, 0.8);

        var rows = analytics.BuildChain(DemoRequest(smile));

        // 每档 InputVol 即 σ(m) = σ₀ + a·m + b·m²，m = ln(K/F)
        foreach (var row in rows)
            Assert.Equal(smile.Vol(Math.Log(row.Strike / 3500.0)), row.InputVol, 12);
    }

    [Fact]
    public void Summarize_TwoPositions_TotalsEqualQuantityWeightedSum()
    {
        var analytics = new OptionChainAnalytics();
        var positions = new[]
        {
            new OptionPosition(new OptionSymbol("M", Expiry, 3400m, OptionRight.Call), 30, 148.5),
            new OptionPosition(new OptionSymbol("M", Expiry, 3600m, OptionRight.Put), -20, 95.0),
        };

        var summary = analytics.Summarize(positions, DemoRequest(), futuresMultiplier: 10);

        Assert.Equal(2, summary.Rows.Count);
        Assert.Equal(summary.Rows[0].Greeks.Delta + summary.Rows[1].Greeks.Delta, summary.Totals.Delta, 10);
        Assert.Equal(summary.Rows[0].Greeks.Gamma + summary.Rows[1].Greeks.Gamma, summary.Totals.Gamma, 10);
        Assert.Equal(summary.Rows[0].Greeks.Vega + summary.Rows[1].Greeks.Vega, summary.Totals.Vega, 10);
        Assert.Equal(summary.Rows[0].Greeks.Theta + summary.Rows[1].Greeks.Theta, summary.Totals.Theta, 10);

        // 行级 Greeks = 每单位引擎 Greeks × 数量：同一持仓数量放大 30 倍，Delta 同比放大
        var unit = analytics.Summarize(
            [positions[0] with { Quantity = 1 }], DemoRequest(), futuresMultiplier: 10);
        Assert.Equal(unit.Rows[0].Greeks.Delta * 30, summary.Rows[0].Greeks.Delta, 8);
    }

    [Fact]
    public void CreateHedgeAdvice_PositiveNetDelta_SuggestsShortFutures()
    {
        // 取整规则：|净Δ| ÷ 乘数 四舍五入（MidpointRounding.AwayFromZero）；25 吨 ÷ 10 吨/手 = 2.5 → 3 手
        var advice = OptionChainAnalytics.CreateHedgeAdvice(netDelta: 25, futuresMultiplier: 10);

        Assert.Equal(HedgeDirection.ShortFutures, advice.Direction);
        Assert.Equal(3, advice.Lots);
    }

    [Fact]
    public void CreateHedgeAdvice_NegativeNetDelta_SuggestsLongFutures()
    {
        var advice = OptionChainAnalytics.CreateHedgeAdvice(netDelta: -24, futuresMultiplier: 10);

        Assert.Equal(HedgeDirection.LongFutures, advice.Direction);
        Assert.Equal(2, advice.Lots);
    }

    [Fact]
    public void CreateHedgeAdvice_NetDeltaRoundsToZeroLots_SuggestsNoHedge()
    {
        var advice = OptionChainAnalytics.CreateHedgeAdvice(netDelta: 4, futuresMultiplier: 10);

        Assert.Equal(HedgeDirection.None, advice.Direction);
        Assert.Equal(0, advice.Lots);
    }

    [Fact]
    public void Summarize_LongCallPosition_HedgeDirectionMatchesNetDeltaSign()
    {
        var analytics = new OptionChainAnalytics();
        var positions = new[]
        {
            new OptionPosition(new OptionSymbol("M", Expiry, 3500m, OptionRight.Call), 30, 102.0),
        };

        var summary = analytics.Summarize(positions, DemoRequest(), futuresMultiplier: 10);

        // 平值 Call long：净 Delta 为正 → 做空期货对冲，手数 = |净Δ| ÷ 乘数取整
        Assert.True(summary.Totals.Delta > 0);
        Assert.Equal(HedgeDirection.ShortFutures, summary.Hedge.Direction);
        Assert.Equal(
            (int)Math.Round(Math.Abs(summary.Totals.Delta) / 10, MidpointRounding.AwayFromZero),
            summary.Hedge.Lots);
    }
}
