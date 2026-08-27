using TradingClient.Application.Risk;

namespace TradingClient.Application.Tests.Fakes;

/// <summary>可配置拒单结果的规则桩，记录调用次数供短路断言。</summary>
public sealed class StubRiskRule(string ruleName, RiskRejection? rejection) : IPreTradeRiskCheck
{
    public string RuleName { get; } = ruleName;
    public int CheckCallCount { get; private set; }

    public ValueTask<RiskRejection?> CheckAsync(RiskCheckContext context, CancellationToken ct)
    {
        CheckCallCount++;
        return ValueTask.FromResult(rejection);
    }
}

/// <summary>实现下单记账钩子的规则桩。</summary>
public sealed class StubHookRiskRule : IPreTradeRiskCheck, IRiskOrderPlacedHook
{
    public string RuleName => "StubHook";
    public int CheckCallCount { get; private set; }
    public int HookCallCount { get; private set; }

    public ValueTask<RiskRejection?> CheckAsync(RiskCheckContext context, CancellationToken ct)
    {
        CheckCallCount++;
        return ValueTask.FromResult<RiskRejection?>(null);
    }

    public void OnOrderPlaced(RiskCheckContext context) => HookCallCount++;
}

public sealed class FakeRiskAuditSink : IRiskAuditSink
{
    public List<(RiskCheckContext Context, RiskRejection Rejection)> Records { get; } = [];

    public void Record(RiskCheckContext context, RiskRejection rejection) =>
        Records.Add((context, rejection));
}
