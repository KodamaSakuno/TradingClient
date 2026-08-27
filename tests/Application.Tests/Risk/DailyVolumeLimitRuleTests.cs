using TradingClient.Application.Risk;
using TradingClient.Application.Risk.Rules;
using TradingClient.Application.Tests.Fakes;

namespace TradingClient.Application.Tests.Risk;

public class DailyVolumeLimitRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static (DailyVolumeLimitRule Rule, FakeTimeProvider Clock) CreateRule(RiskRuleConfig? config = null)
    {
        var clock = new FakeTimeProvider(T0);
        return (new DailyVolumeLimitRule(RiskTestHelpers.Profile(config), clock), clock);
    }

    [Fact]
    public async Task CheckAsync_NoVolumeToday_Passes()
    {
        var (rule, _) = CreateRule();

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 100m), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_AccumulatedPlusOrderExceedsLimit_Rejects()
    {
        var (rule, _) = CreateRule();
        rule.OnOrderPlaced(RiskTestHelpers.Context(quantity: 60m));

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 50m), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("DAILY_VOLUME_EXCEEDED", rejection.Code);
    }

    [Fact]
    public async Task CheckAsync_AccumulatedPlusOrderEqualsLimit_Passes()
    {
        var (rule, _) = CreateRule();
        rule.OnOrderPlaced(RiskTestHelpers.Context(quantity: 60m));

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 40m), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_VolumeAccumulatesPerSymbol()
    {
        var (rule, _) = CreateRule();
        rule.OnOrderPlaced(RiskTestHelpers.Context(quantity: 90m));

        // 其他 symbol 的当日量不受 BTC 影响
        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(symbol: new Domain.Instruments.SpotSymbol("ETH", "USDT"), quantity: 100m),
            CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_NewDay_ResetsAccumulation()
    {
        var (rule, clock) = CreateRule();
        rule.OnOrderPlaced(RiskTestHelpers.Context(quantity: 100m));
        clock.UtcNow = T0.AddDays(1);

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 100m), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task OnOrderPlaced_NewDay_ReplacesStaleEntry()
    {
        var (rule, clock) = CreateRule();
        rule.OnOrderPlaced(RiskTestHelpers.Context(quantity: 100m));
        clock.UtcNow = T0.AddDays(1);

        rule.OnOrderPlaced(RiskTestHelpers.Context(quantity: 10m));

        var rejection = await rule.CheckAsync(
            RiskTestHelpers.Context(quantity: 90m), CancellationToken.None);
        Assert.Null(rejection);
    }
}
