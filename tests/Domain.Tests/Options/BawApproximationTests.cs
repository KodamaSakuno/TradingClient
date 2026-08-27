using TradingClient.Domain.Instruments;
using TradingClient.Domain.Options;

namespace TradingClient.Domain.Tests.Options;

public class BawApproximationTests
{
    // 容差依据：BAW 二次近似误差随 T 增大，实测对 2000 步二叉树偏差 0.003%–1.4%（T≤2），
    // 深虚值价格绝对值小（~0.02）时相对偏差失真，故用 2% 相对 + 5e-3 绝对地板
    [Theory]
    [InlineData(100, 100, 1.0, 0.05, 0.2)]
    [InlineData(90, 100, 0.5, 0.05, 0.3)]
    [InlineData(110, 100, 0.25, 0.08, 0.4)]
    [InlineData(100, 110, 2.0, 0.05, 0.25)]
    [InlineData(95, 100, 0.1, 0.05, 0.6)]
    public void Price_MatchesBinomialTreeWithinApproximationError(
        double f, double k, double t, double r, double sigma)
    {
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            double tree = BinomialTree.Price(f, k, t, r, sigma, right, steps: 2000);
            double baw = BawApproximation.Price(f, k, t, r, sigma, right);

            Assert.True(Math.Abs(baw - tree) <= 0.02 * tree + 5e-3,
                $"{right} F={f} K={k} T={t} σ={sigma}: tree={tree} baw={baw}");
        }
    }

    [Fact]
    public void Price_DeepInTheMoney_GivesImmediateExerciseValue()
    {
        // 越过临界价格：put F=60（F*≈72）、call F=150（F*≈150 附近）
        Assert.Equal(40, BawApproximation.Price(60, 100, 1, 0.05, 0.2, OptionRight.Put), 1e-10);
        Assert.True(BawApproximation.Price(150, 100, 1, 0.05, 0.2, OptionRight.Call) >= 50 - 1e-10);
    }

    [Fact]
    public void Price_AmericanPremium_NonNegative()
    {
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            double baw = BawApproximation.Price(100, 100, 1, 0.05, 0.2, right);
            double european = Black76.Price(100, 100, 1, 0.05, 0.2, right);

            Assert.True(baw >= european, $"{right}: baw={baw} european={european}");
        }
    }

    [Fact]
    public void Price_AtExpiry_ReturnsIntrinsicValue()
    {
        Assert.Equal(10, BawApproximation.Price(110, 100, 0, 0.05, 0.2, OptionRight.Call));
        Assert.Equal(20, BawApproximation.Price(80, 100, -1, 0.05, 0.2, OptionRight.Put));
    }

    [Fact]
    public void Price_ZeroVolatility_TakesBetterOfExerciseAndHolding()
    {
        Assert.Equal(10, BawApproximation.Price(110, 100, 1, 0.05, 0, OptionRight.Call));
        Assert.Equal(0, BawApproximation.Price(90, 100, 1, 0.05, 0, OptionRight.Call));
    }

    // r≤0 时贴现不缩水，期货期权 call/put 都不会提前行权，退化为 Black-76
    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.02)]
    public void Price_NonPositiveRate_FallsBackToBlack76(double r)
    {
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            double european = Black76.Price(100, 100, 1, r, 0.2, right);
            Assert.Equal(european, BawApproximation.Price(100, 100, 1, r, 0.2, right), 1e-12);
        }
    }
}
