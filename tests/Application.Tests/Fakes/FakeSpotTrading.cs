using System.Reactive.Linq;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Fakes;

public sealed class FakeSpotTrading : ISpotTrading
{
    public string ExchangeId => "Fake";
    public ExchangeCapabilities Capabilities { get; } =
        new(AccountMode.Classic, RequiresInternalTransfers: true, Products: [ProductKind.Spot]);
    public IObservable<ConnectionState> ConnectionStates => Observable.Never<ConnectionState>();
    public IObservable<SpotOrderUpdate> SpotOrderUpdates => Observable.Never<SpotOrderUpdate>();
    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    // 可设置的快照，风控测试用它模拟断线
    public ConnectionState CurrentConnectionState { get; set; } = ConnectionState.Connected;

    public int PlaceCallCount { get; private set; }
    public PlaceSpotOrderRequest? LastPlaceRequest { get; private set; }
    public Result<SpotOrder>? NextPlaceResult { get; set; }

    public int CancelCallCount { get; private set; }
    public Symbol? LastCancelSymbol { get; private set; }
    public string? LastCancelOrderId { get; private set; }
    public Result NextCancelResult { get; set; } = Result.Success();

    public Task<Result<SpotOrder>> PlaceSpotOrderAsync(PlaceSpotOrderRequest req, CancellationToken ct)
    {
        PlaceCallCount++;
        LastPlaceRequest = req;
        var result = NextPlaceResult ?? Result.Success(new SpotOrder(
            "FAKE-1", req.Symbol, req.Side, req.Type, req.Price, req.Quantity,
            FilledQuantity: 0m, OrderStatus.New, DateTimeOffset.UtcNow));
        return Task.FromResult(result);
    }

    public Task<Result> CancelSpotOrderAsync(Symbol symbol, string orderId, CancellationToken ct)
    {
        CancelCallCount++;
        LastCancelSymbol = symbol;
        LastCancelOrderId = orderId;
        return Task.FromResult(NextCancelResult);
    }
}
