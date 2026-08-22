using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Abstractions;

public interface IExchangeConnector
{
    string ExchangeId { get; }

    ExchangeCapabilities Capabilities { get; }

    Task ConnectAsync(CancellationToken ct);

    IObservable<ConnectionState> ConnectionStates { get; }
}
