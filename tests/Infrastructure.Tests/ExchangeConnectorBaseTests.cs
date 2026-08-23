using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Common;

namespace TradingClient.Infrastructure.Tests;

public class ExchangeConnectorBaseTests
{
    private sealed class StubConnector : ExchangeConnectorBase
    {
        public override string ExchangeId => "Stub";
        public override ExchangeCapabilities Capabilities { get; } =
            new(AccountMode.Classic, RequiresInternalTransfers: true, Products: [ProductKind.Spot]);

        public override Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public Task Reconnect(Func<CancellationToken, Task> connectAsync, CancellationToken ct) =>
            ReconnectAsync(connectAsync, maxAttempts: 3, initialBackoff: TimeSpan.FromMilliseconds(1), ct);

        public void Publish(ConnectionState state) => SetConnectionState(state);
    }

    [Fact]
    public async Task ReconnectAsync_SucceedsAfterTransientFailures()
    {
        var connector = new StubConnector();
        var attempts = 0;

        await connector.Reconnect(_ =>
        {
            attempts++;
            return attempts < 3
                ? Task.FromException(new HttpRequestException("connection lost"))
                : Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ReconnectAsync_ExhaustsAttempts_Throws()
    {
        var connector = new StubConnector();
        var attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => connector.Reconnect(_ =>
        {
            attempts++;
            return Task.FromException(new HttpRequestException("connection lost"));
        }, TestContext.Current.CancellationToken));

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ConnectionStates_ReflectsPublishedStates()
    {
        var connector = new StubConnector();
        var stateTask = connector.ConnectionStates
            .FirstAsync(s => s == ConnectionState.Connected)
            .Timeout(TimeSpan.FromSeconds(5))
            .ToTask(TestContext.Current.CancellationToken);

        connector.Publish(ConnectionState.Connected);

        Assert.Equal(ConnectionState.Connected, await stateTask);
    }
}
