using TradingClient.Application.Risk.Rules;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Risk;

public class PriceDeviationRuleTests
{
    private readonly PriceDeviationRule _rule = new(RiskTestHelpers.Profile());

    [Fact]
    public async Task CheckAsync_MarketOrder_Skips()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(type: OrderType.Market, price: null, latestPrice: 100m),
            CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_NullLatestPrice_Skips()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(price: 1_000m, latestPrice: null), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_DeviationWithinThreshold_Passes()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(price: 104m, latestPrice: 100m), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_DeviationExactlyAtThreshold_Passes()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(price: 105m, latestPrice: 100m), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_DeviationAboveThreshold_Rejects()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(price: 105.01m, latestPrice: 100m), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("PRICE_DEVIATION_EXCEEDED", rejection.Code);
    }

    [Fact]
    public async Task CheckAsync_DownwardDeviationAboveThreshold_Rejects()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(price: 94.9m, latestPrice: 100m), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("PRICE_DEVIATION_EXCEEDED", rejection.Code);
    }
}
