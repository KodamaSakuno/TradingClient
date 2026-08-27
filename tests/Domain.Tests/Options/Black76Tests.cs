using TradingClient.Domain.Instruments;
using TradingClient.Domain.Options;

namespace TradingClient.Domain.Tests.Options;

public class Black76Tests
{
    // 复算：d1 = (ln(100/100) + 0.2²·1/2)/(0.2·1) = 0.1，d2 = 0.1 − 0.2 = −0.1
    // N(0.1) = 0.5398278373，N(−0.1) = 0.4601721627，e^(−0.05) = 0.9512294245
    // call = 0.9512294245 × 100 × (0.5398278373 − 0.4601721627) = 7.577082
    // F=K 时 put 与 call 等值（put-call parity 特例）。
    // 容差 1e-5：正态 CDF 走 erfc 多项式近似（精度 ~1e-7 量级），价格绝对误差实测 ~5e-6。
    [Fact]
    public void Price_AtTheMoneyForward_MatchesHandComputedValue()
    {
        const double expected = 0.9512294245 * 100 * (0.5398278373 - 0.4601721627);

        double call = Black76.Price(100, 100, 1, 0.05, 0.2, OptionRight.Call);
        double put = Black76.Price(100, 100, 1, 0.05, 0.2, OptionRight.Put);

        Assert.Equal(expected, call, 1e-5);
        Assert.Equal(expected, put, 1e-5);
    }

    // Greeks 复算（同组参数 F=K=100, T=1, r=0.05, σ=0.2）：
    // Delta = e^(−0.05)·N(0.1) = 0.5135002
    // Gamma = e^(−0.05)·n(0.1)/(100·0.2·1) = 0.3775935/20 = 0.0188797，其中 n(0.1) = 0.3969525474
    // Vega  = e^(−0.05)·100·n(0.1)·1/100 = 0.3775935（1 个波动率百分点）
    // Theta = (r·V − e^(−rT)·F·n(d1)·σ/(2√T))/365 = (0.3788542 − 3.7759347)/365 = −0.0093071（自然日）
    // Rho   = −T·V/100 = −0.0757708（1 个利率百分点）
    [Fact]
    public void Greeks_AtTheMoneyForward_MatchesHandComputedValue()
    {
        var greeks = Black76.Greeks(100, 100, 1, 0.05, 0.2, OptionRight.Call);

        Assert.Equal(0.5135002, greeks.Delta, 1e-6);
        Assert.Equal(0.0188797, greeks.Gamma, 1e-6);
        Assert.Equal(0.3775935, greeks.Vega, 1e-6);
        Assert.Equal(-0.0093071, greeks.Theta, 1e-6);
        Assert.Equal(-0.0757708, greeks.Rho, 1e-6);
    }

    [Theory]
    [InlineData(100, 100, 1.0, 0.05, 0.2)]
    [InlineData(90, 100, 0.5, 0.03, 0.3)]
    [InlineData(110, 95, 0.25, 0.08, 0.5)]
    [InlineData(100, 110, 2.0, 0.0, 0.15)]
    public void Price_PutCallParity_Holds(double f, double k, double t, double r, double sigma)
    {
        double call = Black76.Price(f, k, t, r, sigma, OptionRight.Call);
        double put = Black76.Price(f, k, t, r, sigma, OptionRight.Put);

        Assert.Equal(Math.Exp(-r * t) * (f - k), call - put, 1e-10);
    }

    [Fact]
    public void Price_Call_IncreasesWithForward()
    {
        double p90 = Black76.Price(90, 100, 1, 0.05, 0.2, OptionRight.Call);
        double p100 = Black76.Price(100, 100, 1, 0.05, 0.2, OptionRight.Call);
        double p110 = Black76.Price(110, 100, 1, 0.05, 0.2, OptionRight.Call);

        Assert.True(p90 < p100 && p100 < p110);
    }

    [Fact]
    public void Price_Call_DecreasesWithStrike()
    {
        double k90 = Black76.Price(100, 90, 1, 0.05, 0.2, OptionRight.Call);
        double k100 = Black76.Price(100, 100, 1, 0.05, 0.2, OptionRight.Call);
        double k110 = Black76.Price(100, 110, 1, 0.05, 0.2, OptionRight.Call);

        Assert.True(k90 > k100 && k100 > k110);
    }

    [Fact]
    public void Price_BothRights_IncreaseWithVolatility()
    {
        foreach (var right in new[] { OptionRight.Call, OptionRight.Put })
        {
            double low = Black76.Price(100, 100, 1, 0.05, 0.1, right);
            double high = Black76.Price(100, 100, 1, 0.05, 0.4, right);

            Assert.True(low < high);
        }
    }

    [Fact]
    public void Price_AtExpiry_ReturnsIntrinsicValue()
    {
        Assert.Equal(10, Black76.Price(110, 100, 0, 0.05, 0.2, OptionRight.Call));
        Assert.Equal(0, Black76.Price(110, 100, 0, 0.05, 0.2, OptionRight.Put));
        Assert.Equal(20, Black76.Price(80, 100, -0.5, 0.05, 0.2, OptionRight.Put));
    }

    [Fact]
    public void Price_ZeroVolatility_ReturnsDiscountedIntrinsic()
    {
        double expected = Math.Exp(-0.05) * 10;

        Assert.Equal(expected, Black76.Price(110, 100, 1, 0.05, 0, OptionRight.Call), 1e-12);
    }

    [Fact]
    public void Price_DegenerateInput_GreeksReturnZero()
    {
        var atExpiry = Black76.Greeks(110, 100, 0, 0.05, 0.2, OptionRight.Call);
        var zeroVol = Black76.Greeks(110, 100, 1, 0.05, 0, OptionRight.Call);

        Assert.Equal(new OptionGreeks(0, 0, 0, 0, 0), atExpiry);
        Assert.Equal(new OptionGreeks(0, 0, 0, 0, 0), zeroVol);
    }

    [Fact]
    public void Price_NonPositiveForwardOrStrike_Throws()
    {
        Assert.Throws<ArgumentException>(() => Black76.Price(0, 100, 1, 0.05, 0.2, OptionRight.Call));
        Assert.Throws<ArgumentException>(() => Black76.Price(100, -1, 1, 0.05, 0.2, OptionRight.Put));
    }
}
