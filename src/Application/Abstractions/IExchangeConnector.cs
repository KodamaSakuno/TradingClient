using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Abstractions;

public interface IExchangeConnector
{
    string ExchangeId { get; }

    ExchangeCapabilities Capabilities { get; }

    Task ConnectAsync(CancellationToken ct);

    IObservable<ConnectionState> ConnectionStates { get; }

    // 连接状态快照：ConnectionStates 是推送流，风控等场景需要直接读当前值
    ConnectionState CurrentConnectionState { get; }
}
