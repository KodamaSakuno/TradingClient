namespace TradingClient.Application.UseCases.Options;

/// <summary>
/// 演示用参数化微笑：σ(m) = σ₀ + a·m + b·m²，m = ln(K/F) 为对数货币性。
/// 二次偏斜足以展示微笑/偏斜形态对链路定价与 Greeks 的影响；真实波动率曲面拟合（SABR/插值）是增强项。
/// </summary>
public sealed record SmileParameters(double AtmVol, double Skew, double Curvature)
{
    public double Vol(double logMoneyness)
        => AtmVol + Skew * logMoneyness + Curvature * logMoneyness * logMoneyness;
}
