using TradingClient.Domain.Trading;

namespace TradingClient.Application.Risk.Rules;

/// <summary>
/// 断线保护的拒单侧：连接未就绪即拒单。
/// §6.4 的"断线自动撤销未成交单（kill switch）"需订单跟踪能力，本切片不接。
/// </summary>
public sealed class ConnectionGuardRule : IPreTradeRiskCheck
{
    public string RuleName => "ConnectionGuard";

    public ValueTask<RiskRejection?> CheckAsync(RiskCheckContext context, CancellationToken ct) =>
        ValueTask.FromResult(context.Connector.CurrentConnectionState != ConnectionState.Connected
            ? new RiskRejection(RuleName, "NOT_CONNECTED",
                $"Connector {context.Connector.ExchangeId} is {context.Connector.CurrentConnectionState}.")
            : (RiskRejection?)null);
}
