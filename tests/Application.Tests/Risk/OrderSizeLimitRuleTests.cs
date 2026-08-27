using TradingClient.Application.Risk;
using TradingClient.Application.Risk.Rules;

namespace TradingClient.Application.Tests.Risk;

public class OrderSizeLimitRuleTests
{
    [Fact]
    public async Task CheckAsync_QuantityWithinLimit_Passes()
    {
        var rule = new OrderSizeLimitRule(RiskTestHelpers.Profile());

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 10m), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_QuantityExceedsLimit_Rejects()
    {
        var rule = new OrderSizeLimitRule(RiskTestHelpers.Profile());

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 10.001m), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("ORDER_SIZE_EXCEEDED", rejection.Code);
    }

    [Fact]
    public async Task CheckAsync_PerSymbolOverride_UsesOverrideLimit()
    {
        var profile = new RiskLimitsProfile(
            RiskTestHelpers.DefaultConfig,
            new Dictionary<string, RiskRuleConfig>
            {
                [RiskTestHelpers.BtcUsdt.Raw] = RiskTestHelpers.DefaultConfig with { MaxOrderQuantity = 0.5m },
            });
        var rule = new OrderSizeLimitRule(profile);

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 1m), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("ORDER_SIZE_EXCEEDED", rejection.Code);
    }
}
