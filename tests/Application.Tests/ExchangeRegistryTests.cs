using System.Reactive.Linq;
using TradingClient.Application.Abstractions;
using TradingClient.Application.Services;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests;

public class ExchangeRegistryTests
{
    private sealed class StubConnector(string exchangeId) : IExchangeConnector
    {
        public string ExchangeId => exchangeId;
        public ExchangeCapabilities Capabilities { get; } =
            new(AccountMode.Classic, RequiresInternalTransfers: true, Products: [ProductKind.Spot]);
        public IObservable<ConnectionState> ConnectionStates => Observable.Never<ConnectionState>();
        public ConnectionState CurrentConnectionState => ConnectionState.Connected;
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void TryGet_ReturnsRegisteredConnector()
    {
        var registry = new ExchangeRegistry();
        var connector = new StubConnector("gate");

        registry.Register(connector);

        Assert.True(registry.TryGet("gate", out var found));
        Assert.Same(connector, found);
    }

    [Fact]
    public void Lookup_IsCaseInsensitive()
    {
        var registry = new ExchangeRegistry();
        registry.Register(new StubConnector("Gate"));

        Assert.True(registry.TryGet("GATE", out _));
        Assert.True(registry.TryGet("gate", out _));
    }

    [Fact]
    public void TryGet_ReturnsFalseForUnknownExchange()
    {
        var registry = new ExchangeRegistry();

        Assert.False(registry.TryGet("binance", out var connector));
        Assert.Null(connector);
    }

    [Fact]
    public void GetRequired_ThrowsForUnknownExchange()
    {
        var registry = new ExchangeRegistry();

        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("binance"));
    }

    [Fact]
    public void Register_OverwritesExistingExchangeId()
    {
        var registry = new ExchangeRegistry();
        var first = new StubConnector("gate");
        var second = new StubConnector("gate");

        registry.Register(first);
        registry.Register(second);

        Assert.Single(registry.All);
        Assert.Same(second, registry.GetRequired("gate"));
    }

    [Fact]
    public void Register_WithNull_Throws()
    {
        var registry = new ExchangeRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }
}
