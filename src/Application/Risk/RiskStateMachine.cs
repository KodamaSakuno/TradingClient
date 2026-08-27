using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace TradingClient.Application.Risk;

/// <summary>
/// 风控状态机（§6.4）：事前/事中两层风控的咬合点——监控写状态，事前闸门读状态。
/// 共享单例，由 Composition Root 注册。同状态迁移为空操作（不迁移不广播）。
/// 迁移即广播 Transitions 并写审计。本切片暂无写入方（IRiskMonitor 为下一切片），靠 TransitionTo 单测覆盖。
/// </summary>
public sealed class RiskStateMachine(IRiskAuditSink audit)
{
    private readonly object _gate = new();
    private readonly Subject<RiskStateTransition> _transitions = new();

    public RiskState Current { get; private set; } = RiskState.Normal;

    public IObservable<RiskStateTransition> Transitions => _transitions.AsObservable();

    public void TransitionTo(RiskState to, string reason)
    {
        RiskStateTransition transition;
        lock (_gate)
        {
            if (Current == to)
                return;
            transition = new RiskStateTransition(Current, to, reason, DateTimeOffset.UtcNow);
            Current = to;
        }
        // 先审计后广播：订阅者（UI 告警）收到迁移时审计必然已落
        audit.RecordStateTransition(transition);
        _transitions.OnNext(transition);
    }
}
