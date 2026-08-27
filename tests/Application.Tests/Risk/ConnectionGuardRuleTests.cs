using TradingClient.Application.Risk.Rules;
using TradingClient.Application.Tests.Fakes;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Risk;

public class ConnectionGuardRuleTests
{
    private readonly ConnectionGuardRule _rule = new();

    [Fact]
    public async Task CheckAsync_Connected_Passes()
    {
        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(connector: new FakeSpotTrading()), CancellationToken.None);

        Assert.Null(rejection);
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Reconnecting)]
    public async Task CheckAsync_NotConnected_RejectsWithNotConnected(ConnectionState state)
    {
        var connector = new FakeSpotTrading { CurrentConnectionState = state };

        var rejection = await _rule.CheckAsync(
            RiskTestHelpers.Context(connector: connector), CancellationToken.None);

        Assert.NotNull(rejection);
        Assert.Equal("NOT_CONNECTED", rejection.Code);
        Assert.Equal("ConnectionGuard", rejection.RuleName);
    }
}
