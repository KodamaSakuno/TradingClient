using TradingClient.Application.Risk;
using TradingClient.Application.Risk.Rules;
using TradingClient.Application.Tests.Fakes;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Risk;

public class RiskStateGateRuleTests
{
    private static readonly PerpetualFuturesSymbol BtcUsdtPerp = new("BTC", "USDT");

    private readonly RiskStateMachine _machine = new(new FakeRiskAuditSink());
    private readonly RiskStateGateRule _rule;

    public RiskStateGateRuleTests() => _rule = new RiskStateGateRule(_machine);

    [Theory]
    [InlineData(RiskState.Normal)]
    [InlineData(RiskState.Warning)]
    public async Task CheckAsync_NormalOrWarning_Passes(RiskState state)
    {
        _machine.TransitionTo(state, "test");

        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(symbol: BtcUsdtPerp), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_Locked_RejectsAllOrders()
    {
        _machine.TransitionTo(RiskState.Locked, "kill switch");

        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(symbol: BtcUsdtPerp, position: 5m), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("RISK_LOCKED", rejection.Code);
        Assert.Equal("RiskStateGate", rejection.RuleName);
    }

    [Fact]
    public async Task CheckAsync_ReduceOnlyReducingOrder_Passes()
    {
        _machine.TransitionTo(RiskState.ReduceOnly, "limit hit");

        // 持空 2，买入 1 = 减空
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(symbol: BtcUsdtPerp, side: OrderSide.Buy, quantity: 1m, position: -2m),
            CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_ReduceOnlyIncreasingOrder_Rejects()
    {
        _machine.TransitionTo(RiskState.ReduceOnly, "limit hit");

        // 持多 1，买入 = 加多
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(symbol: BtcUsdtPerp, side: OrderSide.Buy, quantity: 1m, position: 1m),
            CancellationToken.None);

        AssertReduceOnlyRejection(rejection);
    }

    [Fact]
    public async Task CheckAsync_ReduceOnlyQuantityExceedsPosition_Rejects()
    {
        _machine.TransitionTo(RiskState.ReduceOnly, "limit hit");

        // 持空 1，买入 2 超出持仓量（超出部分是加仓）
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(symbol: BtcUsdtPerp, side: OrderSide.Buy, quantity: 2m, position: -1m),
            CancellationToken.None);

        AssertReduceOnlyRejection(rejection);
    }

    [Fact]
    public async Task CheckAsync_ReduceOnlyWithoutPositionSnapshot_FailsClosed()
    {
        _machine.TransitionTo(RiskState.ReduceOnly, "limit hit");

        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(symbol: BtcUsdtPerp, position: null), CancellationToken.None);

        var rejected = AssertReduceOnlyRejection(rejection);
        Assert.Contains("position snapshot", rejected.Reason);
    }

    [Fact]
    public async Task CheckAsync_ReduceOnlySpotSell_Passes()
    {
        _machine.TransitionTo(RiskState.ReduceOnly, "limit hit");

        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(symbol: RiskTestHelpers.BtcUsdt, side: OrderSide.Sell),
            CancellationToken.None);

        Assert.Null(rejection);
    }

    [Fact]
    public async Task CheckAsync_ReduceOnlySpotBuy_Rejects()
    {
        _machine.TransitionTo(RiskState.ReduceOnly, "limit hit");

        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(symbol: RiskTestHelpers.BtcUsdt, side: OrderSide.Buy),
            CancellationToken.None);

        AssertReduceOnlyRejection(rejection);
    }

    private static RiskRejection AssertReduceOnlyRejection(RiskRejection? rejection)
    {
        Assert.NotNull(rejection);
        Assert.Equal("RISK_REDUCE_ONLY", rejection.Code);
        Assert.Equal("RiskStateGate", rejection.RuleName);
        return rejection;
    }
}
