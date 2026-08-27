using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Options;

/// <summary>
/// Black-76 期货期权欧式定价。美式期权存在提前行权溢价，Black-76 给出的是欧式下界；
/// 美式定价由 <see cref="BinomialTree"/>（基准）与 <see cref="BawApproximation"/>（快速近似）负责，
/// "美式价 − 欧式价 = 提前行权溢价" 由上层对照展示。
/// </summary>
public static class Black76
{
    /// <summary>
    /// 定价。T≤0 或 σ≤0 时退化为贴现内在价值：到期时期权按内在价值结算，σ=0 时期货路径确定，两者同义。
    /// F/K 非正属于编程错误（价格不可能非正），直接抛异常而非走 Result。
    /// </summary>
    public static double Price(
        double forward, double strike, double timeToExpiry,
        double rate, double volatility, OptionRight right)
    {
        if (forward <= 0)
            throw new ArgumentException("标的期货价格必须为正", nameof(forward));
        if (strike <= 0)
            throw new ArgumentException("行权价必须为正", nameof(strike));

        double discount = Discount(rate, timeToExpiry);

        if (timeToExpiry <= 0 || volatility <= 0)
        {
            double intrinsic = right == OptionRight.Call
                ? Math.Max(forward - strike, 0)
                : Math.Max(strike - forward, 0);
            return discount * intrinsic;
        }

        var (d1, d2) = D1D2(forward, strike, timeToExpiry, volatility);
        return right == OptionRight.Call
            ? discount * (forward * NormalCdf(d1) - strike * NormalCdf(d2))
            : discount * (strike * NormalCdf(-d2) - forward * NormalCdf(-d1));
    }

    /// <summary>
    /// 解析 Greeks。退化情形（T≤0 或 σ≤0）时间价值为零、Greeks 无定义，返回全零。
    /// 期货期权的 Rho 只有贴现渠道：∂V/∂r = −T·V（标的不漂移，r 不进入 d1/d2），故 Rho = −T·V/100。
    /// Theta 按自然日：Θ = −∂V/∂T/365，其中 ∂V/∂T = −r·V + e^(−rT)·F·n(d1)·σ/(2√T)。
    /// </summary>
    public static OptionGreeks Greeks(
        double forward, double strike, double timeToExpiry,
        double rate, double volatility, OptionRight right)
    {
        if (timeToExpiry <= 0 || volatility <= 0)
            return new OptionGreeks(0, 0, 0, 0, 0);

        double discount = Discount(rate, timeToExpiry);
        var (d1, _) = D1D2(forward, strike, timeToExpiry, volatility);
        double sqrtT = Math.Sqrt(timeToExpiry);
        double pdf = NormalPdf(d1);
        double price = Price(forward, strike, timeToExpiry, rate, volatility, right);

        double delta = right == OptionRight.Call
            ? discount * NormalCdf(d1)
            : -discount * NormalCdf(-d1);
        double gamma = discount * pdf / (forward * volatility * sqrtT);
        double vega = discount * forward * pdf * sqrtT / 100;
        double theta = (rate * price - discount * forward * pdf * volatility / (2 * sqrtT)) / 365;
        double rho = -timeToExpiry * price / 100;

        return new OptionGreeks(delta, gamma, vega, theta, rho);
    }

    // T 为负（已过期）时按 0 贴现，避免负指数放大价值
    internal static double Discount(double rate, double timeToExpiry)
        => Math.Exp(-rate * Math.Max(timeToExpiry, 0));

    internal static (double D1, double D2) D1D2(double forward, double strike, double timeToExpiry, double volatility)
    {
        double volSqrtT = volatility * Math.Sqrt(timeToExpiry);
        double d1 = (Math.Log(forward / strike) + volatility * volatility * timeToExpiry / 2) / volSqrtT;
        return (d1, d1 - volSqrtT);
    }

    internal static double NormalPdf(double x)
        => Math.Exp(-0.5 * x * x) / Math.Sqrt(2 * Math.PI);

    internal static double NormalCdf(double x)
        => 0.5 * Erfc(-x / Math.Sqrt(2));

    // erfc 用 A&S 7.1.26 系的多项式写法（t = 1/(1+z/2)，十项），全区间相对误差 < 1.2e-7。
    // 不用同书绝对误差 1.5e-7 的 erf 形式：深度虚值时 N(d) ~ 1e-11，
    // 绝对误差会吞掉真实值，导致隐含波动率反解无法收敛。
    private static double Erfc(double x)
    {
        double z = Math.Abs(x);
        double t = 1.0 / (1.0 + 0.5 * z);
        double ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196
            + t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398
            + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0.0 ? ans : 2.0 - ans;
    }
}
