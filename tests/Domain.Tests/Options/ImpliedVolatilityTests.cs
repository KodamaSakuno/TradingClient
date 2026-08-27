using TradingClient.Domain.Instruments;
using TradingClient.Domain.Options;

namespace TradingClient.Domain.Tests.Options;

public class ImpliedVolatilityTests
{
    // 网格往返：BAW 美式定价 → 反解 → |σ̂−σ| < 1e-4。
    // BAW 价格面光滑且 Vega 在网格内均远离 0，Newton 直接收敛。
    // 例外：深实值且低 σ/短 T 时美式价钉在未贴现内在价值上（F 已越过临界价 F*），
    // 价格对 σ 不敏感、IV 不可识别，这些组合跳过（如实值 put F=90, σ=0.05, T=0.1）。
    [Fact]
    public void Solve_BawAmericanPrices_RoundTripsVolatility()
    {
        int skipped = 0;
        foreach (double f in new[] { 90.0, 100.0, 110.0 })
        foreach (double t in new[] { 0.1, 0.5, 1.0 })
        foreach (double sigma in new[] { 0.05, 0.2, 0.8 })
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            double price = BawApproximation.Price(f, 100, t, 0.05, sigma, right);
            double intrinsic = Math.Max(right == OptionRight.Call ? f - 100 : 100 - f, 0);
            if (price - intrinsic < 1e-6)
            {
                skipped++;
                continue;
            }

            var result = ImpliedVolatility.Solve(BawApproximation.Price, price, f, 100, t, 0.05, right);

            Assert.True(result.IsSuccess, $"F={f} T={t} σ={sigma} {right}: {result.Error?.Code}");
            Assert.Equal(sigma, result.Value, 1e-4);
        }
        Assert.True(skipped > 0, "网格应覆盖到钉在内在价值上的组合");
    }

    // 欧式树委托冒烟：树价有离散噪声（500 步 ~4e-3），σ 误差 ≈ 噪声/Vega，容差放宽到 2e-3
    [Fact]
    public void Solve_TreeEuropeanDelegate_RecoversVolatility()
    {
        AmericanGreeks.PricingFunction treeEuropean =
            (f, k, t, r, sigma, right) => BinomialTree.Price(f, k, t, r, sigma, right, 500, false);
        double price = Black76.Price(100, 100, 1, 0.05, 0.2, OptionRight.Call);

        var result = ImpliedVolatility.Solve(treeEuropean, price, 100, 100, 1, 0.05, OptionRight.Call);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.2, result.Value, 2e-3);
    }

    [Fact]
    public void Solve_PriceBelowDiscountedIntrinsic_ReturnsBelowIntrinsic()
    {
        // 实值 put 的贴现内在价值 = e^(−0.025)·10 ≈ 9.753，目标价取 9 触发无套利下界
        var result = ImpliedVolatility.Solve(
            BawApproximation.Price, 9, 90, 100, 0.5, 0.05, OptionRight.Put);

        Assert.False(result.IsSuccess);
        Assert.Equal("BELOW_INTRINSIC", result.Error?.Code);
    }

    // 深虚值：价格 ~5e-3 且小 σ 处 Vega≈0，Newton 步不可用，走二分兜底路径
    [Fact]
    public void Solve_DeepOutOfTheMoney_BisectionPathConverges()
    {
        double price = BawApproximation.Price(100, 140, 0.2, 0.05, 0.25, OptionRight.Call);

        var result = ImpliedVolatility.Solve(BawApproximation.Price, price, 100, 140, 0.2, 0.05, OptionRight.Call);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.25, result.Value, 1e-3);
    }

    [Theory]
    [InlineData(0, 100, 1.0)]    // F 非正
    [InlineData(100, 0, 1.0)]    // K 非正
    [InlineData(100, 100, 0.0)]  // T 非正
    public void Solve_InvalidInput_ReturnsInvalidInput(double f, double k, double t)
    {
        var result = ImpliedVolatility.Solve(BawApproximation.Price, 5, f, k, t, 0.05, OptionRight.Call);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_INPUT", result.Error?.Code);
    }

    [Fact]
    public void Solve_NegativeTargetPrice_ReturnsInvalidInput()
    {
        var result = ImpliedVolatility.Solve(BawApproximation.Price, -1, 100, 100, 1, 0.05, OptionRight.Call);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_INPUT", result.Error?.Code);
    }

    [Fact]
    public void Solve_PriceAboveMaxVolatilityBound_ReturnsNoConvergence()
    {
        // call 价格硬上界 = e^(−rT)·F ≈ 95.12，σ→∞ 也只能逼近它；取 95.5 必无解
        var result = ImpliedVolatility.Solve(BawApproximation.Price, 95.5, 100, 100, 1, 0.05, OptionRight.Call);

        Assert.False(result.IsSuccess);
        Assert.Equal("NO_CONVERGENCE", result.Error?.Code);
    }
}
