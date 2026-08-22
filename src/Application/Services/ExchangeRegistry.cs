using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using TradingClient.Application.Abstractions;

namespace TradingClient.Application.Services;

public sealed class ExchangeRegistry
{
    private readonly ConcurrentDictionary<string, IExchangeConnector> _connectors =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IExchangeConnector> All => _connectors.Values.ToArray();

    public void Register(IExchangeConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        _connectors[connector.ExchangeId] = connector;
    }

    public bool TryGet(string exchangeId, [NotNullWhen(true)] out IExchangeConnector? connector) =>
        _connectors.TryGetValue(exchangeId, out connector);

    public IExchangeConnector GetRequired(string exchangeId) =>
        _connectors.GetValueOrDefault(exchangeId) ?? throw new KeyNotFoundException($"Unregistered exchange id: {exchangeId}");
}
