using TradingClient.Application.Risk.Rules;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Risk;

public class PositionLimitRuleTests
{
    private readonly PositionLimitRule _rule = new(RiskTestHelpers.Profile());

    [Fact]
    public async Task CheckAsync_NullPositionSnapshot_Skips()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 1_000m, position: null), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_BuyProjectionWithinLimit_Passes()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 10m, position: 40m), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_BuyProjectionExceedsLimit_Rejects()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 11m, position: 40m), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("POSITION_LIMIT_EXCEEDED", rejection.Code);
    }

    [Fact]
    public async Task CheckAsync_SellProjectionExceedsLimit_Rejects()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(side: OrderSide.Sell, quantity: 20m, position: -40m),
            CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("POSITION_LIMIT_EXCEEDED", rejection.Code);
    }

    [Fact]
    public async Task CheckAsync_SellReducesLongPosition_Passes()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(side: OrderSide.Sell, quantity: 45m, position: 40m),
            CancellationToken.None);

        Assert.Null(rejection);
    }
}
