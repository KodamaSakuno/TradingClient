using TradingClient.Application.Tests.Fakes;
using TradingClient.Application.UseCases.Spot;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;

namespace TradingClient.Application.Tests;

public class CancelSpotOrderTests
{
    private static readonly SpotSymbol BtcUsdt = new("BTC", "USDT");

    [Fact]
    public async Task ExecuteAsync_ValidRequest_ForwardsToGateway()
    {
        var trading = new FakeSpotTrading();
        var useCase = new CancelSpotOrder(trading);

        var result = await useCase.ExecuteAsync(BtcUsdt, "GATE-42", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, trading.CancelCallCount);
        Assert.Equal(BtcUsdt, trading.LastCancelSymbol);
        Assert.Equal("GATE-42", trading.LastCancelOrderId);
    }

    [Fact]
    public async Task ExecuteAsync_GatewayFailure_PropagatesError()
    {
        var trading = new FakeSpotTrading
        {
            NextCancelResult = Result.Failure(new ExchangeError("ORDER_NOT_FOUND", "Unknown order id.")),
        };
        var useCase = new CancelSpotOrder(trading);

        var result = await useCase.ExecuteAsync(BtcUsdt, "GATE-42", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("ORDER_NOT_FOUND", result.Error!.Code);
    }
}
