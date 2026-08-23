using System.Reactive.Subjects;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Exchanges.Common;

public abstract class ExchangeConnectorBase : IExchangeConnector
{
    private const int MaxReconnectAttempts = 10;

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly BehaviorSubject<ConnectionState> _connectionStates = new(ConnectionState.Disconnected);

    public abstract string ExchangeId { get; }
    public abstract ExchangeCapabilities Capabilities { get; }
    public IObservable<ConnectionState> ConnectionStates => _connectionStates;

    public abstract Task ConnectAsync(CancellationToken ct);

    protected void SetConnectionState(ConnectionState state) => _connectionStates.OnNext(state);

    protected Task ReconnectAsync(Func<CancellationToken, Task> connectAsync, CancellationToken ct) =>
        ReconnectAsync(connectAsync, MaxReconnectAttempts, InitialBackoff, ct);

    protected async Task ReconnectAsync(
        Func<CancellationToken, Task> connectAsync, int maxAttempts, TimeSpan initialBackoff, CancellationToken ct)
    {
        SetConnectionState(ConnectionState.Reconnecting);
        var backoff = initialBackoff;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connectAsync(ct);
                return;
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }
}
