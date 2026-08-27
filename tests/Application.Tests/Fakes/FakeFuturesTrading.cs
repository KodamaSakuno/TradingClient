using System.Reactive.Linq;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Fakes;

public sealed class FakeFuturesTrading : IFuturesTrading
{
    public string ExchangeId => "Fake";
    public ExchangeCapabilities Capabilities { get; } =
        new(AccountMode.Classic, RequiresInternalTransfers: true, Products: [ProductKind.Futures]);
    public IObservable<ConnectionState> ConnectionStates => Observable.Never<ConnectionState>();
    public ConnectionState CurrentConnectionState { get; set; } = ConnectionState.Connected;
    public IObservable<PositionUpdate> PositionUpdates => Observable.Never<PositionUpdate>();
    public IObservable<LiquidationWarning> LiquidationWarnings => Observable.Never<LiquidationWarning>();
    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public int PlaceCallCount { get; private set; }
    public PlaceFuturesOrderRequest? LastPlaceRequest { get; private set; }
    public Result<FuturesOrder>? NextPlaceResult { get; set; }

    public Task<Result<FuturesOrder>> PlaceFuturesOrderAsync(PlaceFuturesOrderRequest req, CancellationToken ct)
    {
        PlaceCallCount++;
        LastPlaceRequest = req;
        var result = NextPlaceResult ?? Result.Success(new FuturesOrder(
            "FAKE-1", req.Symbol, req.Side, req.Type, req.Price, req.Quantity,
            FilledQuantity: 0m, OrderStatus.New, req.PositionSide, req.MarginMode, DateTimeOffset.UtcNow));
        return Task.FromResult(result);
    }

    public Task<Result> SetLeverageAsync(Symbol symbol, int leverage, MarginMode mode, CancellationToken ct) =>
        Task.FromResult(Result.Success());

    public Task<Result> SetPositionModeAsync(PositionMode mode, CancellationToken ct) =>
        Task.FromResult(Result.Success());

    public Task<Result<IReadOnlyList<Position>>> GetPositionsAsync(CancellationToken ct) =>
        Task.FromResult(Result.Success<IReadOnlyList<Position>>([]));
}
