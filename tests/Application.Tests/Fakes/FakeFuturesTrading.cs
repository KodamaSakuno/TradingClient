using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Fakes;

public sealed class FakeFuturesTrading : IFuturesTrading
{
    private readonly Subject<PositionUpdate> _positionUpdates = new();
    private readonly Subject<ConnectionState> _connectionStates = new();

    public string ExchangeId => "Fake";
    public ExchangeCapabilities Capabilities { get; } =
        new(AccountMode.Classic, RequiresInternalTransfers: true, Products: [ProductKind.Futures]);
    public IObservable<ConnectionState> ConnectionStates => _connectionStates.AsObservable();
    public ConnectionState CurrentConnectionState { get; set; } = ConnectionState.Connected;
    public IObservable<PositionUpdate> PositionUpdates => _positionUpdates.AsObservable();
    public IObservable<LiquidationWarning> LiquidationWarnings => Observable.Never<LiquidationWarning>();
    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    public int PlaceCallCount { get; private set; }
    public PlaceFuturesOrderRequest? LastPlaceRequest { get; private set; }
    public Result<FuturesOrder>? NextPlaceResult { get; set; }

    public int CancelAllCallCount { get; private set; }
    public Result? NextCancelAllResult { get; set; }

    // 供 RiskMonitor 测试回放持仓/连接流
    public void PushPosition(Position position) =>
        _positionUpdates.OnNext(new PositionUpdate(position, DateTimeOffset.UtcNow));

    public void PushConnectionState(ConnectionState state) => _connectionStates.OnNext(state);

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

    public Task<Result> CancelAllFuturesOrdersAsync(CancellationToken ct)
    {
        CancelAllCallCount++;
        return Task.FromResult(NextCancelAllResult ?? Result.Success());
    }
}
