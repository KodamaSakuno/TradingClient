using TradingClient.Application.Risk;
using TradingClient.Application.Tests.Fakes;

namespace TradingClient.Application.Tests.Risk;

public class RiskStateMachineTests
{
    [Fact]
    public void TransitionTo_SameState_DoesNotBroadcastOrAudit()
    {
        var audit = new FakeRiskAuditSink();
        var machine = new RiskStateMachine(audit);
        var observed = new List<RiskStateTransition>();
        machine.Transitions.Subscribe(observed.Add);

        machine.TransitionTo(RiskState.Normal, "already normal");

        Assert.Equal(RiskState.Normal, machine.Current);
        Assert.Empty(observed);
        Assert.Empty(audit.Transitions);
    }

    [Fact]
    public void TransitionTo_DifferentState_BroadcastsTransitionAndAudits()
    {
        var audit = new FakeRiskAuditSink();
        var machine = new RiskStateMachine(audit);
        var observed = new List<RiskStateTransition>();
        machine.Transitions.Subscribe(observed.Add);

        machine.TransitionTo(RiskState.ReduceOnly, "daily loss limit hit");

        Assert.Equal(RiskState.ReduceOnly, machine.Current);

        var broadcast = Assert.Single(observed);
        Assert.Equal(RiskState.Normal, broadcast.From);
        Assert.Equal(RiskState.ReduceOnly, broadcast.To);
        Assert.Equal("daily loss limit hit", broadcast.Reason);

        // 审计与广播承载同一条迁移
        var audited = Assert.Single(audit.Transitions);
        Assert.Equal(broadcast, audited);
    }

    [Fact]
    public void TransitionTo_MultipleTransitions_BroadcastsEachInOrder()
    {
        var machine = new RiskStateMachine(new FakeRiskAuditSink());
        var observed = new List<RiskStateTransition>();
        machine.Transitions.Subscribe(observed.Add);

        machine.TransitionTo(RiskState.Warning, "approaching limit");
        machine.TransitionTo(RiskState.Locked, "kill switch");

        Assert.Equal(RiskState.Locked, machine.Current);
        Assert.Equal(
            [(RiskState.Normal, RiskState.Warning), (RiskState.Warning, RiskState.Locked)],
            observed.Select(t => (t.From, t.To)).ToArray());
    }
}
