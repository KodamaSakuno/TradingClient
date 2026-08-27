using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Fakes;

public sealed class FakeMarketData : IMarketData
{
    private readonly ConcurrentDictionary<ProductKind, int> _callCounts = new();
    private readonly Dictionary<ProductKind, IReadOnlyList<Instrument>> _instruments = new();
    private readonly Subject<Quote> _quotes = new();

    public string ExchangeId => "Fake";
    public ExchangeCapabilities Capabilities { get; } =
        new(AccountMode.Classic, RequiresInternalTransfers: true, Products: [ProductKind.Spot]);
    public IObservable<ConnectionState> ConnectionStates => Observable.Never<ConnectionState>();
    public ConnectionState CurrentConnectionState { get; set; } = ConnectionState.Connected;
    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    // 非零延迟用于并发测试，让多个首次加载调用真正重叠
    public TimeSpan LoadDelay { get; set; } = TimeSpan.Zero;

    // RiskMonitor 测试用：活动行情订阅数（验证 Symbol 消失退订）
    public int ActiveQuoteSubscriptions { get; private set; }

    public int CallCount(ProductKind product) => _callCounts.GetValueOrDefault(product);

    public void SetInstruments(ProductKind product, params Instrument[] instruments) =>
        _instruments[product] = instruments;

    public void PushQuote(Quote quote) => _quotes.OnNext(quote);

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(ProductKind product, CancellationToken ct)
    {
        _callCounts.AddOrUpdate(product, 1, (_, n) => n + 1);
        if (LoadDelay > TimeSpan.Zero)
            await Task.Delay(LoadDelay, ct);
        return _instruments.GetValueOrDefault(product) ?? [];
    }

    public IObservable<Quote> SubscribeQuotes(Symbol symbol) => Observable.Create<Quote>(observer =>
    {
        ActiveQuoteSubscriptions++;
        var inner = _quotes.Where(q => q.Symbol.Raw == symbol.Raw).Subscribe(observer);
        return () =>
        {
            ActiveQuoteSubscriptions--;
            inner.Dispose();
        };
    });

    public IObservable<Trade> SubscribeTrades(Symbol symbol) => Observable.Never<Trade>();
    public IObservable<OrderBookDelta> SubscribeOrderBook(Symbol symbol) => Observable.Never<OrderBookDelta>();
    public IObservable<Candle> SubscribeCandles(Symbol symbol, TimeFrame tf) => Observable.Never<Candle>();
}
