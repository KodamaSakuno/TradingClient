namespace TradingClient.Domain.Options;

/// <summary>
/// 期权 Greeks。Vega 按 1 个波动率百分点（σ 变动 0.01）的价值变动，
/// Theta 按自然日（时间流逝一天）的价值变动，Rho 按 1 个利率百分点（r 变动 0.01）的价值变动。
/// </summary>
public sealed record OptionGreeks(double Delta, double Gamma, double Vega, double Theta, double Rho);
