using TradingClient.Application.Risk.Rules;
using TradingClient.Application.Tests.Fakes;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Risk;

public class DuplicateOrderRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static (DuplicateOrderRule Rule, FakeTimeProvider Clock) CreateRule()
    {
        var clock = new FakeTimeProvider(T0);
        return (new DuplicateOrderRule(RiskTestHelpers.Profile(), clock), clock);
    }

    [Fact]
    public async Task CheckAsync_FirstSubmission_Passes()
    {
        var (rule, _) = CreateRule();

        var rejection = await rule.CheckAsync(RiskTestHelpers.Context(), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_SameSymbolSidePriceWithinWindow_Rejects()
    {
        var (rule, _) = CreateRule();
        await rule.CheckAsync(RiskTestHelpers.Context(price: 100m), CancellationToken.None);

        var rejection = await rule.CheckAsync(RiskTestHelpers.Context(price: 100m), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("DUPLICATE_ORDER", rejection.Code);
    }

    [Fact]
    public async Task CheckAsync_PriceWithinTolerance_Rejects()
    {
        var (rule, _) = CreateRule();
        await rule.CheckAsync(RiskTestHelpers.Context(price: 100m), CancellationToken.None);

        // 容差 0.1%：100 × 0.001 = 0.1，100.05 落在容差内
        var rejection = await rule.CheckAsync(RiskTestHelpers.Context(price: 100.05m), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("DUPLICATE_ORDER", rejection.Code);
    }

    [Fact]
    public async Task CheckAsync_PriceOutsideTolerance_Passes()
    {
        var (rule, _) = CreateRule();
        await rule.CheckAsync(RiskTestHelpers.Context(price: 100m), CancellationToken.None);

        var rejection = await rule.CheckAsync(RiskTestHelpers.Context(price: 100.2m), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_DifferentSide_Passes()
    {
        var (rule, _) = CreateRule();
        await rule.CheckAsync(RiskTestHelpers.Context(price: 100m), CancellationToken.None);

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(side: OrderSide.Sell, price: 100m), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_DifferentSymbol_Passes()
    {
        var (rule, _) = CreateRule();
        await rule.CheckAsync(RiskTestHelpers.Context(price: 100m), CancellationToken.None);

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(symbol: new Domain.Instruments.SpotSymbol("ETH", "USDT"), price: 100m),
            CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_OutsideWindow_Passes()
    {
        var (rule, clock) = CreateRule();
        await rule.CheckAsync(RiskTestHelpers.Context(price: 100m), CancellationToken.None);

        clock.UtcNow = T0.AddSeconds(3.1);
        var rejection = await rule.CheckAsync(RiskTestHelpers.Context(price: 100m), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_MarketOrderTwiceWithinWindow_Rejects()
    {
        var (rule, _) = CreateRule();
        await rule.CheckAsync(
            RiskTestHelpers.Context(type: OrderType.Market, price: null), CancellationToken.None);

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(type: OrderType.Market, price: null), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("DUPLICATE_ORDER", rejection.Code);
    }
}
