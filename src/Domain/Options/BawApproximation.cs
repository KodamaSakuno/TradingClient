using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Options;

/// <summary>
/// BAW（Barone-Adesi-Whaley 1987）美式期货期权二次近似：Black-76 欧式价 + 提前行权修正项。
/// 供 IV 反解与 T 型报价批量定价等高频调用；精度对照 CRR 二叉树由测试兜底，
/// 经典文献与实测的近似误差量级在小数百分比内（见 BawApproximationTests 注释）。
/// 公式按期货期权持有成本 b=0 特化（N = 2b/σ² = 0），临界价格 F* 用 Newton 迭代求解
/// （初值取 Haug《The Complete Guide to Option Pricing Formulas》给出的种子）。
/// </summary>
public static class BawApproximation
{
    private const int MaxIterations = 100;
    private const double Tolerance = 1e-8;

    /// <summary>
    /// 定价。退化处理：
    /// T≤0 → 到期内在价值；σ≤0 → 路径确定，取立即行权与持有到期的较大者（与二叉树一致）；
    /// r≤0 → 贴现不缩水，提前行权拿不到利息收益，call/put 都不会提前行权，退化为 Black-76；
    /// Newton 不收敛（病态参数）→ 兜底返回 Black-76 欧式价。
    /// </summary>
    public static double Price(
        double forward, double strike, double timeToExpiry,
        double rate, double volatility, OptionRight right)
    {
        if (forward <= 0)
            throw new ArgumentException("标的期货价格必须为正", nameof(forward));
        if (strike <= 0)
            throw new ArgumentException("行权价必须为正", nameof(strike));

        double intrinsic = right == OptionRight.Call
            ? Math.Max(forward - strike, 0)
            : Math.Max(strike - forward, 0);

        if (timeToExpiry <= 0)
            return intrinsic;

        if (volatility <= 0)
            return Math.Max(intrinsic, Black76.Discount(rate, timeToExpiry) * intrinsic);

        if (rate <= 0)
            return Black76.Price(forward, strike, timeToExpiry, rate, volatility, right);

        return right == OptionRight.Call
            ? CallPrice(forward, strike, timeToExpiry, rate, volatility)
            : PutPrice(forward, strike, timeToExpiry, rate, volatility);
    }

    private static double CallPrice(double forward, double strike, double t, double r, double sigma)
    {
        double m = 2 * r / (sigma * sigma);
        double kt = 1 - Math.Exp(-r * t);
        // call 修正项须随 F 增大而增大，取正根；put 相反取负根
        double q = (1 + Math.Sqrt(1 + 4 * m / kt)) / 2;

        double critical = SolveCriticalPrice(strike, t, r, sigma, q, OptionRight.Call);
        if (double.IsNaN(critical))
            return Black76.Price(forward, strike, t, r, sigma, OptionRight.Call);

        if (forward >= critical)
            return forward - strike;

        double discount = Math.Exp(-r * t);
        var (d1, _) = Black76.D1D2(critical, strike, t, sigma);
        double a1 = critical / q * (1 - discount * Black76.NormalCdf(d1));
        return Black76.Price(forward, strike, t, r, sigma, OptionRight.Call)
            + a1 * Math.Pow(forward / critical, q);
    }

    private static double PutPrice(double forward, double strike, double t, double r, double sigma)
    {
        double m = 2 * r / (sigma * sigma);
        double kt = 1 - Math.Exp(-r * t);
        double q = (1 - Math.Sqrt(1 + 4 * m / kt)) / 2;

        double critical = SolveCriticalPrice(strike, t, r, sigma, q, OptionRight.Put);
        if (double.IsNaN(critical))
            return Black76.Price(forward, strike, t, r, sigma, OptionRight.Put);

        if (forward <= critical)
            return strike - forward;

        double discount = Math.Exp(-r * t);
        var (d1, _) = Black76.D1D2(critical, strike, t, sigma);
        double a2 = -critical / q * (1 - discount * Black76.NormalCdf(-d1));
        return Black76.Price(forward, strike, t, r, sigma, OptionRight.Put)
            + a2 * Math.Pow(forward / critical, q);
    }

    // Newton + 二分兜底求解临界价格 F*：F* 处满足价值匹配（立即行权价值 = 欧式价 + 修正项）。
    // q 为正根（call，F* 在 K 上方）或负根（put，F* 在 K 下方）。
    // 初值用 Haug 的永续临界价种子；裸 Newton 在深实值区会来回超调（g 近乎线性、斜率小），
    // 故用区间夹逼：g(K)<0，call 上界取永续临界价（F* 随 T 递增，必在其内），put 下界取 0+。
    // 不收敛返回 NaN，由调用方兜底 Black-76。
    private static double SolveCriticalPrice(
        double strike, double t, double r, double sigma, double q, OptionRight right)
    {
        double m = 2 * r / (sigma * sigma);
        double discount = Math.Exp(-r * t);
        double sqrtT = Math.Sqrt(t);

        double qInf = right == OptionRight.Call
            ? (1 + Math.Sqrt(1 + 4 * m)) / 2
            : (1 - Math.Sqrt(1 + 4 * m)) / 2;
        double criticalInf = strike / (1 - 1 / qInf);
        double h = right == OptionRight.Call
            ? -2 * sigma * sqrtT * strike / (criticalInf - strike)
            : -2 * sigma * sqrtT * strike / (strike - criticalInf);
        double f = right == OptionRight.Call
            ? strike + (criticalInf - strike) * (1 - Math.Exp(h))
            : criticalInf + (strike - criticalInf) * Math.Exp(h);

        double lo = right == OptionRight.Call ? strike : strike * 1e-9;
        double hi = right == OptionRight.Call ? criticalInf : strike;

        for (int i = 0; i < MaxIterations; i++)
        {
            double european = Black76.Price(f, strike, t, r, sigma, right);
            var (d1, _) = Black76.D1D2(f, strike, t, sigma);
            double pdf = Black76.NormalPdf(d1);

            double g, gPrime;
            if (right == OptionRight.Call)
            {
                double n = Black76.NormalCdf(d1);
                g = f - strike - european - f / q * (1 - discount * n);
                gPrime = (1 - 1 / q) * (1 - discount * n) - discount * pdf / (q * sigma * sqrtT);
            }
            else
            {
                double n = Black76.NormalCdf(-d1);
                g = strike - f - european + f / q * (1 - discount * n);
                gPrime = -(1 - 1 / q) * (1 - discount * n) + discount * pdf / (q * sigma * sqrtT);
            }

            if (Math.Abs(g) / strike < Tolerance)
                return f;

            // g 在 call 情形单调增、put 情形单调减，区间更新方向据此区分
            if (g > 0 == (right == OptionRight.Call)) hi = f; else lo = f;

            double next = f - g / gPrime;
            if (Math.Abs(gPrime) < 1e-12 || next <= lo || next >= hi || double.IsNaN(next))
                next = (lo + hi) / 2;

            f = next;
        }

        return double.NaN;
    }
}
