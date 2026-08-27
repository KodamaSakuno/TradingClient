using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Options;

/// <summary>
/// 美式期权数值 Greeks：bump-and-revalue，中心差分，对任意定价委托可用（二叉树 / BAW）。
/// 步长是截断误差与定价器数值噪声的折中：树价面随参数呈锯齿（节点离散），步长太小会被噪声淹没；
/// 1e-3 的相对/绝对步长在光滑定价器（BAW）上的截断误差仍在 1e-6 量级以内。
/// Theta 约定按自然日：直接以一天（1/365 年）为差分窗口，Theta = V(T−h) − V(T+h) 除以两天。
/// </summary>
public static class AmericanGreeks
{
    public delegate double PricingFunction(
        double forward, double strike, double timeToExpiry,
        double rate, double volatility, OptionRight right);

    private const double RelativeForwardStep = 1e-3;
    private const double VolatilityStep = 1e-3;
    private const double RateStep = 1e-3;

    public static OptionGreeks Greeks(
        PricingFunction price,
        double forward, double strike, double timeToExpiry,
        double rate, double volatility, OptionRight right)
    {
        double hF = Math.Max(forward * RelativeForwardStep, 1e-8);
        double up = price(forward + hF, strike, timeToExpiry, rate, volatility, right);
        double mid = price(forward, strike, timeToExpiry, rate, volatility, right);
        double down = price(forward - hF, strike, timeToExpiry, rate, volatility, right);
        double delta = (up - down) / (2 * hF);
        double gamma = (up - 2 * mid + down) / (hF * hF);

        double vega = (price(forward, strike, timeToExpiry, rate, volatility + VolatilityStep, right)
            - price(forward, strike, timeToExpiry, rate, volatility - VolatilityStep, right))
            / (2 * VolatilityStep) / 100;

        // 剩余期限不足两天时窗口减半，避免 T−h 为负
        double hT = Math.Min(1.0 / 365, timeToExpiry / 4);
        double theta = hT > 0
            ? (price(forward, strike, timeToExpiry - hT, rate, volatility, right)
                - price(forward, strike, timeToExpiry + hT, rate, volatility, right)) / (2 * hT) / 365
            : 0;

        double rho = (price(forward, strike, timeToExpiry, rate + RateStep, volatility, right)
            - price(forward, strike, timeToExpiry, rate - RateStep, volatility, right))
            / (2 * RateStep) / 100;

        return new OptionGreeks(delta, gamma, vega, theta, rho);
    }
}
