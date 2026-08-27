using TradingClient.Domain.Instruments;
using TradingClient.Domain.Options;

namespace TradingClient.Domain.Tests.Options;

public class AmericanGreeksTests
{
    // 用 Black-76 作定价委托验证 bump-and-revalue 机器本身：
    // 中心差分截断误差 O(h²)，1e-3 步长下实测偏差 ~1e-6，容差取 1e-4
    [Fact]
    public void Greeks_Black76Delegate_MatchesAnalytic()
    {
        var analytic = Black76.Greeks(100, 100, 1, 0.05, 0.2, OptionRight.Call);
        var numeric = AmericanGreeks.Greeks(Black76.Price, 100, 100, 1, 0.05, 0.2, OptionRight.Call);

        Assert.Equal(analytic.Delta, numeric.Delta, 1e-4);
        Assert.Equal(analytic.Gamma, numeric.Gamma, 1e-4);
        Assert.Equal(analytic.Vega, numeric.Vega, 1e-4);
        Assert.Equal(analytic.Theta, numeric.Theta, 1e-5);
        Assert.Equal(analytic.Rho, numeric.Rho, 1e-4);
    }

    // 美式引擎跑欧式模式（二叉树 american=false）对照 Black-76 解析 Greeks。
    // 容差放宽依据：树价面随参数呈锯齿（节点离散，500 步价差噪声 ~1e-3），
    // bump 差分会放大噪声；实测 2000 步 Delta/Vega/Theta/Rho 偏差 ≤ 1e-4，容差取 1e-3。
    // Gamma 是二阶差分（噪声 ÷ hF²），实测偏差可达真值的数倍，无法在此引擎上验证，故豁免。
    [Fact]
    public void Greeks_TreeEuropeanMode_ApproximatesAnalytic()
    {
        AmericanGreeks.PricingFunction treeEuropean =
            (f, k, t, r, sigma, right) => BinomialTree.Price(f, k, t, r, sigma, right, 2000, false);

        var analytic = Black76.Greeks(100, 100, 1, 0.05, 0.2, OptionRight.Call);
        var numeric = AmericanGreeks.Greeks(treeEuropean, 100, 100, 1, 0.05, 0.2, OptionRight.Call);

        Assert.Equal(analytic.Delta, numeric.Delta, 1e-3);
        Assert.Equal(analytic.Vega, numeric.Vega, 1e-3);
        Assert.Equal(analytic.Theta, numeric.Theta, 1e-4);
        Assert.Equal(analytic.Rho, numeric.Rho, 1e-3);
    }

    // 美式 Greeks  sanity：BAW 定价下 put Delta 为负、Gamma/Vega 为正，
    // 且美式 put 的 |Delta| 大于欧式（提前行权边界使价值曲线更陡）
    [Fact]
    public void Greeks_BawAmericanPut_SignAndMagnitudeSane()
    {
        var european = Black76.Greeks(100, 100, 1, 0.05, 0.2, OptionRight.Put);
        var american = AmericanGreeks.Greeks(BawApproximation.Price, 100, 100, 1, 0.05, 0.2, OptionRight.Put);

        Assert.True(american.Delta < 0);
        Assert.True(american.Gamma > 0);
        Assert.True(american.Vega > 0);
        Assert.True(Math.Abs(american.Delta) > Math.Abs(european.Delta));
    }
}
