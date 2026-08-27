using TradingClient.Domain.Instruments;
using TradingClient.Domain.Options;

namespace TradingClient.Domain.Tests.Options;

public class BinomialTreeTests
{
    // 容差依据：CRR 未做奇偶步平均，价格围绕真值以 O(1/n) 震荡；
    // 实测 500 步欧式模式误差 ≤ 4e-3（三组参数），容差取 5e-3
    [Theory]
    [InlineData(100, 100, 1.0, 0.05, 0.2)]
    [InlineData(90, 100, 0.5, 0.05, 0.3)]
    [InlineData(110, 95, 0.25, 0.03, 0.5)]
    public void Price_EuropeanMode_ConvergesToBlack76(double f, double k, double t, double r, double sigma)
    {
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            double european = Black76.Price(f, k, t, r, sigma, right);
            double tree = BinomialTree.Price(f, k, t, r, sigma, right, steps: 500, american: false);

            Assert.Equal(european, tree, 5e-3);
        }
    }

    // 提前行权溢价非负；树离散噪声可致微小负值（实测最差 −1.3e-5），容差放到 1e-4
    [Theory]
    [InlineData(100, 100, 1.0, 0.05, 0.2)]
    [InlineData(90, 100, 0.5, 0.05, 0.3)]
    [InlineData(110, 95, 0.25, 0.08, 0.4)]
    [InlineData(95, 100, 2.0, 0.03, 0.25)]
    public void Price_American_NotBelowEuropean(double f, double k, double t, double r, double sigma)
    {
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            double european = Black76.Price(f, k, t, r, sigma, right);
            double american = BinomialTree.Price(f, k, t, r, sigma, right);

            Assert.True(american >= european - 1e-4,
                $"{right}: american={american} european={european}");
        }
    }

    // 期货期权 call/put 两侧都可能提前行权（行权即拿内在价值吃利息），深度实值溢价显著
    [Fact]
    public void Price_DeepInTheMoney_EarlyExercisePremiumSignificant()
    {
        // 深实值 put：立即行权拿 40，欧式仅贴现值 ≈ 38.07
        double putAmerican = BinomialTree.Price(60, 100, 1, 0.05, 0.2, OptionRight.Put);
        double putEuropean = Black76.Price(60, 100, 1, 0.05, 0.2, OptionRight.Put);
        Assert.True(putAmerican - putEuropean > 1.5, $"put premium = {putAmerican - putEuropean}");

        // 深实值 call：立即行权拿 50，欧式 ≈ 47.74
        double callAmerican = BinomialTree.Price(150, 100, 1, 0.05, 0.2, OptionRight.Call);
        double callEuropean = Black76.Price(150, 100, 1, 0.05, 0.2, OptionRight.Call);
        Assert.True(callAmerican - callEuropean > 2, $"call premium = {callAmerican - callEuropean}");
    }

    // 实测（F=K=100, T=1, r=0.05, σ=0.2）：欧式 |err| 100步=1.9e-2 / 500步=3.8e-3 / 2000步=9.5e-4；
    // 美式相邻差 |Δ(100,500)|=1.3e-2 > |Δ(500,2000)|=2.4e-3，2000 步距 8000 步参考值 6e-4
    [Fact]
    public void Price_MoreSteps_ConvergesMonotonically()
    {
        double european = Black76.Price(100, 100, 1, 0.05, 0.2, OptionRight.Call);
        double err100 = Math.Abs(BinomialTree.Price(100, 100, 1, 0.05, 0.2, OptionRight.Call, 100, false) - european);
        double err500 = Math.Abs(BinomialTree.Price(100, 100, 1, 0.05, 0.2, OptionRight.Call, 500, false) - european);
        double err2000 = Math.Abs(BinomialTree.Price(100, 100, 1, 0.05, 0.2, OptionRight.Call, 2000, false) - european);

        Assert.True(err500 < err100 && err2000 < err500,
            $"err: 100={err100} 500={err500} 2000={err2000}");

        double p100 = BinomialTree.Price(100, 100, 1, 0.05, 0.2, OptionRight.Put, 100);
        double p500 = BinomialTree.Price(100, 100, 1, 0.05, 0.2, OptionRight.Put, 500);
        double p2000 = BinomialTree.Price(100, 100, 1, 0.05, 0.2, OptionRight.Put, 2000);

        Assert.True(Math.Abs(p2000 - p500) < Math.Abs(p500 - p100),
            $"american deltas: {Math.Abs(p500 - p100)} vs {Math.Abs(p2000 - p500)}");
    }

    [Fact]
    public void Price_AtExpiry_ReturnsIntrinsicValue()
    {
        Assert.Equal(10, BinomialTree.Price(110, 100, 0, 0.05, 0.2, OptionRight.Call));
        Assert.Equal(0, BinomialTree.Price(110, 100, 0, 0.05, 0.2, OptionRight.Put));
    }

    [Fact]
    public void Price_ZeroVolatility_AmericanTakesImmediateExercise()
    {
        // 路径确定（F 不动）且 r≥0：美式立即行权优于持有到期
        Assert.Equal(10, BinomialTree.Price(110, 100, 1, 0.05, 0, OptionRight.Call));
        Assert.Equal(Math.Exp(-0.05) * 10,
            BinomialTree.Price(110, 100, 1, 0.05, 0, OptionRight.Call, american: false), 1e-12);
    }
}
