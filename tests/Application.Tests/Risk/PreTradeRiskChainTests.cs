using TradingClient.Application.Risk;
using TradingClient.Application.Tests.Fakes;

namespace TradingClient.Application.Tests.Risk;

public class PreTradeRiskChainTests
{
    [Fact]
    public async Task CheckAsync_AllRulesPass_ReturnsSuccess()
    {
        var audit = new FakeRiskAuditSink();
        var chain = new PreTradeRiskChain(
            [new StubRiskRule("A", null), new StubRiskRule("B", null)], audit);

        var result = await chain.CheckAsync(RiskTestHelpers.Context(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(audit.Records);
    }

    [Fact]
    public async Task CheckAsync_FirstRuleRejects_ShortCircuitsSecondRule()
    {
        var first = new StubRiskRule("First", new RiskRejection("First", "ERR_A", "reason A"));
        var second = new StubRiskRule("Second", new RiskRejection("Second", "ERR_B", "reason B"));
        var chain = new PreTradeRiskChain([first, second], new FakeRiskAuditSink());

        var result = await chain.CheckAsync(RiskTestHelpers.Context(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, first.CheckCallCount);
        Assert.Equal(0, second.CheckCallCount);
    }

    [Fact]
    public async Task CheckAsync_Rejection_AuditsRuleNameAndReason()
    {
        var audit = new FakeRiskAuditSink();
        var rejection = new RiskRejection("DailyVolumeLimit", "DAILY_VOLUME_EXCEEDED", "over the limit");
        var chain = new PreTradeRiskChain([new StubRiskRule("DailyVolumeLimit", rejection)], audit);

        var result = await chain.CheckAsync(RiskTestHelpers.Context(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("DAILY_VOLUME_EXCEEDED", result.Error!.Code);
        Assert.Equal("[DailyVolumeLimit] over the limit", result.Error.Message);
        var record = Assert.Single(audit.Records);
        Assert.Equal("DailyVolumeLimit", record.Rejection.RuleName);
        Assert.Equal("over the limit", record.Rejection.Reason);
    }

    [Fact]
    public void NotifyOrderPlaced_OnlyNotifiesHookImplementingRules()
    {
        var plain = new StubRiskRule("Plain", null);
        var hook = new StubHookRiskRule();
        var chain = new PreTradeRiskChain([plain, hook], new FakeRiskAuditSink());

        chain.NotifyOrderPlaced(RiskTestHelpers.Context());

        Assert.Equal(0, plain.CheckCallCount);
        Assert.Equal(1, hook.HookCallCount);
    }
}
