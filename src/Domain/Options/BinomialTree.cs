using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Options;

/// <summary>
/// CRR 二叉树，美式期货期权定价基准实现。
/// 期货无漂移：风险中性增长因子 a = e^(b·Δt) 中持有成本 b=0，故 p = (a − d)/(u − d) = (1 − d)/(u − d)。
/// 奇偶步数价格会围绕真值震荡，未做相邻步数平均（Richardson 简版）：步数参数由调用方控制，
/// 一致性测试的容差已按此放宽（见 BinomialTreeTests 注释）。
/// </summary>
public static class BinomialTree
{
    public const int DefaultSteps = 500;

    /// <summary>
    /// 定价。american=false 时跳过提前行权检查（欧式模式），用于"树收敛到 Black-76"的一致性测试。
    /// T≤0 时按到期内在价值结算；σ≤0 时期货路径确定（F 不动），美式价值取"立即行权"与
    /// "持有到到期"的较大者：r≥0 时立即行权，r&lt;0 时贴现反而放大价值、持有更优。
    /// </summary>
    public static double Price(
        double forward, double strike, double timeToExpiry,
        double rate, double volatility, OptionRight right,
        int steps = DefaultSteps, bool american = true)
    {
        if (forward <= 0)
            throw new ArgumentException("标的期货价格必须为正", nameof(forward));
        if (strike <= 0)
            throw new ArgumentException("行权价必须为正", nameof(strike));
        if (steps < 1)
            throw new ArgumentException("步数必须为正", nameof(steps));

        double intrinsic = Intrinsic(forward, strike, right);

        if (timeToExpiry <= 0)
            return intrinsic;

        double discount = Black76.Discount(rate, timeToExpiry);

        if (volatility <= 0)
            return american ? Math.Max(intrinsic, discount * intrinsic) : discount * intrinsic;

        double dt = timeToExpiry / steps;
        double u = Math.Exp(volatility * Math.Sqrt(dt));
        double d = 1 / u;
        double p = (1 - d) / (u - d);
        double stepDiscount = Math.Exp(-rate * dt);

        // 节点 j（j 次上涨）的期货价：F·u^j·d^(i−j)。终端层从 F·d^n 起逐节点乘 u/d = u²，避免逐点 Pow。
        double uu = u * u;
        var values = new double[steps + 1];
        double nodePrice = forward * Math.Pow(d, steps);
        for (int j = 0; j <= steps; j++)
        {
            values[j] = Intrinsic(nodePrice, strike, right);
            nodePrice *= uu;
        }

        for (int i = steps - 1; i >= 0; i--)
        {
            nodePrice = forward * Math.Pow(d, i);
            for (int j = 0; j <= i; j++)
            {
                double hold = stepDiscount * (p * values[j + 1] + (1 - p) * values[j]);
                values[j] = american
                    ? Math.Max(hold, Intrinsic(nodePrice, strike, right))
                    : hold;
                nodePrice *= uu;
            }
        }

        return values[0];
    }

    private static double Intrinsic(double forward, double strike, OptionRight right)
        => right == OptionRight.Call
            ? Math.Max(forward - strike, 0)
            : Math.Max(strike - forward, 0);
}
