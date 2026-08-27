using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;

namespace TradingClient.Domain.Options;

/// <summary>
/// 隐含波动率反解：Newton 迭代（数值 Vega 定步长）+ 二分兜底。
/// 定价委托可注入美式引擎（BAW 或二叉树），反解出的即美式 IV。
/// 搜索区间 σ ∈ [1e-4, 5.0]：下限贴近"确定性路径"退化，上限已远超商品期权实务波动率。
/// </summary>
public static class ImpliedVolatility
{
    public const double MinVolatility = 1e-4;
    public const double MaxVolatility = 5.0;

    private const int MaxIterations = 100;
    private const double VegaStep = 1e-4;

    /// <summary>
    /// 由市价反解 σ。错误码：
    /// INVALID_INPUT —— F/K/T 非正或目标价为负；
    /// BELOW_INTRINSIC —— 目标价低于贴现内在价值（无套利下界），返回明确错误而非 NaN；
    /// NO_CONVERGENCE —— 区间内有解但迭代未收敛，或目标价超出 σ=5.0 对应的价格上界。
    /// 注意美式价的无套利下界其实是未贴现内在价值；介于贴现与未贴现之间的目标价
    /// 对美式委托无解，会走 NO_CONVERGENCE，此处不单独设错误码。
    /// </summary>
    public static Result<double> Solve(
        AmericanGreeks.PricingFunction price,
        double targetPrice,
        double forward, double strike, double timeToExpiry,
        double rate, OptionRight right)
    {
        if (forward <= 0 || strike <= 0 || timeToExpiry <= 0)
            return Result.Failure<double>(new ExchangeError("INVALID_INPUT", "标的价格、行权价、剩余期限必须为正"));
        if (double.IsNaN(targetPrice) || targetPrice < 0)
            return Result.Failure<double>(new ExchangeError("INVALID_INPUT", "目标价格非法"));

        double intrinsic = right == OptionRight.Call
            ? Math.Max(forward - strike, 0)
            : Math.Max(strike - forward, 0);
        double lowerBound = Black76.Discount(rate, timeToExpiry) * intrinsic;
        if (targetPrice < lowerBound - 1e-10 * Math.Max(1, lowerBound))
            return Result.Failure<double>(new ExchangeError("BELOW_INTRINSIC", "目标价低于贴现内在价值，违反无套利下界"));

        double upperBound = price(forward, strike, timeToExpiry, rate, MaxVolatility, right);
        if (targetPrice > upperBound)
            return Result.Failure<double>(new ExchangeError("NO_CONVERGENCE", "目标价超出波动率搜索区间上界对应的价格"));

        double lo = MinVolatility, hi = MaxVolatility;
        double sigma = 0.3;
        double tolerance = 1e-10 * Math.Max(1, targetPrice);

        for (int i = 0; i < MaxIterations; i++)
        {
            double value = price(forward, strike, timeToExpiry, rate, sigma, right);
            double diff = value - targetPrice;

            if (Math.Abs(diff) < tolerance)
                return Result.Success(sigma);

            // 价格随 σ 单调增，用残差符号维护二分区间
            if (diff > 0) hi = sigma; else lo = sigma;

            double vega = (price(forward, strike, timeToExpiry, rate, sigma + VegaStep, right)
                - price(forward, strike, timeToExpiry, rate, sigma - VegaStep, right)) / (2 * VegaStep);

            // 深虚值 Vega≈0 或 Newton 步跳出区间时退化为二分
            double next = sigma - diff / vega;
            if (vega < 1e-12 || next <= lo || next >= hi || double.IsNaN(next))
                next = (lo + hi) / 2;

            if (Math.Abs(next - sigma) < 1e-12)
                return Result.Success(next);

            sigma = next;
        }

        return Result.Failure<double>(new ExchangeError("NO_CONVERGENCE", "隐含波动率迭代未收敛"));
    }
}
