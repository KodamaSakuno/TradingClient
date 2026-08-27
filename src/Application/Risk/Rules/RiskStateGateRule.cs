using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Risk.Rules;

/// <summary>
/// 风控状态闸门：读共享 RiskStateMachine，在链里排最前（状态级检查最便宜）。
/// Normal / Warning 放行——Warning 只告警不拦，告警由状态迁移广播负责。
/// Locked 全拒；ReduceOnly 仅放行减仓单，减仓判定靠 CurrentPositionQuantity 的带符号视角
/// （正=多、负=空；Buy 加多/减空，Sell 反之），方向相反且 |订单量| ≤ |持仓量| 才算减仓。
/// 持仓快照为 null 时 fail-closed 拒单：风控闸门宁可误拦不可放行。
/// 现货无持仓概念，ReduceOnly 语义简化为只允许卖出（卖出=减现货库存），买入拒绝。
/// </summary>
public sealed class RiskStateGateRule(RiskStateMachine stateMachine) : IPreTradeRiskCheck
{
    public string RuleName => "RiskStateGate";

    public ValueTask<RiskRejection?> CheckAsync(RiskCheckContext context, CancellationToken ct)
    {
        var rejection = stateMachine.Current switch
        {
            RiskState.Locked => Reject("RISK_LOCKED", "Risk state is Locked; all orders are rejected."),
            RiskState.ReduceOnly => CheckReduceOnly(context),
            _ => null,
        };
        return ValueTask.FromResult(rejection);
    }

    private RiskRejection? CheckReduceOnly(RiskCheckContext context)
    {
        if (context.Symbol.Product == ProductKind.Spot)
            return context.Side == OrderSide.Sell
                ? null
                : Reject("RISK_REDUCE_ONLY", "Risk state is ReduceOnly; spot buys are rejected.");

        if (context.CurrentPositionQuantity is not { } position)
            return Reject("RISK_REDUCE_ONLY",
                "Risk state is ReduceOnly and no position snapshot is available.");

        var reduces = context.Side == OrderSide.Buy
            ? position < 0 && context.Quantity <= -position
            : position > 0 && context.Quantity <= position;
        return reduces
            ? null
            : Reject("RISK_REDUCE_ONLY",
                $"Risk state is ReduceOnly; {context.Side} {context.Quantity} against position {position} does not reduce exposure.");
    }

    private RiskRejection Reject(string code, string reason) => new(RuleName, code, reason);
}
