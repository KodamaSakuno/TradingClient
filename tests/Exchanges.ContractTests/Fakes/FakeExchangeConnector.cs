using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Exchanges.ContractTests.Fakes;

public sealed class FakeExchangeConnector(AccountMode mode)
    : IAccountService, IMarketData, ISpotTrading, IFuturesTrading
{
    private readonly Subject<ConnectionState> _connectionStates = new();
    private readonly Subject<SpotOrderUpdate> _spotOrderUpdates = new();
    private readonly Subject<PositionUpdate> _positionUpdates = new();
    private readonly Subject<LiquidationWarning> _liquidationWarnings = new();

    private readonly Dictionary<string, SpotOrder> _spotOrders = new();
    private readonly List<Position> _positions = [];

    private int _nextOrderId;

    public string ExchangeId => "Fake";
    public ExchangeCapabilities Capabilities { get; } = new(
        mode,
        RequiresInternalTransfers: mode == AccountMode.Classic,
        Products: [ProductKind.Spot, ProductKind.Futures]);

    public IObservable<ConnectionState> ConnectionStates => _connectionStates;

    public Task ConnectAsync(CancellationToken ct)
    {
        _connectionStates.OnNext(ConnectionState.Connected);
        return Task.CompletedTask;
    }

    // --- IAccountService ---

    public Task<Result<AccountSummary>> GetAccountAsync(CancellationToken ct)
    {
        var isUnified = Capabilities.AccountMode == AccountMode.Unified;
        var summary = new AccountSummary(
            Capabilities.AccountMode,
            TotalEquity: 10_000m,
            AvailableMargin: 8_000m,
            InitialMargin: 1_500m,
            MaintenanceMargin: 1_000m,
            MarginRatio: 0.125m,
            Assets:
            [
                new AssetBalance(
                    "USDT", Total: 10_000m, Frozen: 0m,
                    CollateralWeight: isUnified ? 1m : null,
                    EquityValue: 10_000m),
            ]);
        return Task.FromResult(Result.Success(summary));
    }

    public Task<Result> TransferFundsAsync(TransferRequest req, CancellationToken ct)
    {
        return Task.FromResult(Result.Success());
    }

    // --- IMarketData ---

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(ProductKind product, CancellationToken ct)
    {
        IReadOnlyList<Instrument> instruments = product switch
        {
            ProductKind.Spot =>
            [
                new Instrument(new SpotSymbol("BTC", "USDT"), 0.01m, 0.0001m, 0.0001m, null, null, InstrumentStatus.Trading),
                new Instrument(new SpotSymbol("ETH", "USDT"), 0.01m, 0.001m, 0.001m, null, null, InstrumentStatus.Trading),
            ],
            ProductKind.Futures =>
            [
                new Instrument(new PerpetualFuturesSymbol("BTC", "USDT"), 0.1m, 0.001m, 0.001m, null, 0.0001m, InstrumentStatus.Trading),
            ],
            _ => [],
        };
        return Task.FromResult(instruments);
    }

    public IObservable<Quote> SubscribeQuotes(Symbol symbol) => Observable.Never<Quote>();
    public IObservable<Trade> SubscribeTrades(Symbol symbol) => Observable.Never<Trade>();
    public IObservable<OrderBookDelta> SubscribeOrderBook(Symbol symbol) => Observable.Never<OrderBookDelta>();
    public IObservable<Candle> SubscribeCandles(Symbol symbol, TimeFrame tf) => Observable.Never<Candle>();

    // --- ISpotTrading ---

    public IObservable<SpotOrderUpdate> SpotOrderUpdates => _spotOrderUpdates;

    public Task<Result<SpotOrder>> PlaceSpotOrderAsync(PlaceSpotOrderRequest req, CancellationToken ct)
    {
        if (req.Quantity <= 0)
            return Task.FromResult(Result.Failure<SpotOrder>(
                new ExchangeError("INVALID_QUANTITY", "Quantity must be positive.")));
        if (req is { Type: OrderType.Limit, Price: null })
            return Task.FromResult(Result.Failure<SpotOrder>(
                new ExchangeError("MISSING_PRICE", "Limit order requires a price.")));

        var order = new SpotOrder(
            NewOrderId(), req.Symbol, req.Side, req.Type, req.Price,
            req.Quantity, FilledQuantity: 0m, OrderStatus.New, DateTimeOffset.UtcNow);
        _spotOrders[order.OrderId] = order;
        _spotOrderUpdates.OnNext(new SpotOrderUpdate(order, DateTimeOffset.UtcNow));
        return Task.FromResult(Result.Success(order));
    }

    public Task<Result> CancelSpotOrderAsync(Symbol symbol, string orderId, CancellationToken ct)
    {
        return Task.FromResult(_spotOrders.Remove(orderId)
            ? Result.Success()
            : Result.Failure(new ExchangeError("ORDER_NOT_FOUND", $"Unknown order id: {orderId}")));
    }

    // --- IFuturesTrading ---

    public IObservable<PositionUpdate> PositionUpdates => _positionUpdates;
    public IObservable<LiquidationWarning> LiquidationWarnings => _liquidationWarnings;

    public Task<Result<FuturesOrder>> PlaceFuturesOrderAsync(PlaceFuturesOrderRequest req, CancellationToken ct)
    {
        if (req.Quantity <= 0)
            return Task.FromResult(Result.Failure<FuturesOrder>(
                new ExchangeError("INVALID_QUANTITY", "Quantity must be positive.")));
        if (req is { Type: OrderType.Limit, Price: null })
            return Task.FromResult(Result.Failure<FuturesOrder>(
                new ExchangeError("MISSING_PRICE", "Limit order requires a price.")));

        var order = new FuturesOrder(
            NewOrderId(), req.Symbol, req.Side, req.Type, req.Price,
            req.Quantity, FilledQuantity: 0m, OrderStatus.New,
            req.PositionSide, req.MarginMode, DateTimeOffset.UtcNow);

        var position = new Position(
            req.Symbol, req.PositionSide, req.Quantity,
            EntryPrice: req.Price ?? 0m, UnrealizedPnl: 0m,
            Leverage: req.Leverage ?? 1, req.MarginMode);
        _positions.Add(position);
        _positionUpdates.OnNext(new PositionUpdate(position, DateTimeOffset.UtcNow));
        return Task.FromResult(Result.Success(order));
    }

    public Task<Result> SetLeverageAsync(Symbol symbol, int leverage, MarginMode mode, CancellationToken ct)
    {
        return Task.FromResult(leverage > 0
            ? Result.Success()
            : Result.Failure(new ExchangeError("INVALID_LEVERAGE", "Leverage must be positive.")));
    }

    public Task<Result> SetPositionModeAsync(PositionMode mode, CancellationToken ct)
    {
        return Task.FromResult(Result.Success());
    }

    public Task<Result<IReadOnlyList<Position>>> GetPositionsAsync(CancellationToken ct)
    {
        return Task.FromResult(Result.Success<IReadOnlyList<Position>>(_positions.ToArray()));
    }

    private string NewOrderId() => $"FAKE-{Interlocked.Increment(ref _nextOrderId)}";
}
