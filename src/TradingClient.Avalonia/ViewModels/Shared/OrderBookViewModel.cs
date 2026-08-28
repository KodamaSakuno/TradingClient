using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using Serilog;
using TradingClient.Application.Abstractions;
using TradingClient.Application.Services;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Avalonia.ViewModels.Shared;

/// <summary>
/// 订单簿梯子（现货/合约共用）。由 MainWindowViewModel 持有，跟随顶部选择器与 Symbol 流，
/// 切换即退订旧流、丢弃旧盘口（新建 LocalOrderBook）重建。
/// 管道：SubscribeOrderBook → 后台线程 Apply 进本地盘口（埋点计时）→ Sample 150ms
/// → ObserveOn UI 线程 → 两侧各 10 档灌进 SourceCache 供绑定。
/// </summary>
public sealed class OrderBookViewModel : ViewModelBase, IDisposable
{
    private const int Depth = 10;
    private static readonly TimeSpan BookThrottle = TimeSpan.FromMilliseconds(150); // 行情节流 100–200ms

    private readonly CompositeDisposable _subscriptions = new();
    private readonly SourceCache<OrderBookRowViewModel, decimal> _bids = new(r => r.Price);
    private readonly SourceCache<OrderBookRowViewModel, decimal> _asks = new(r => r.Price);

    private int _deltaCount; // 后台线程 Interlocked 递增，UI 线程每秒取样清零
    private double _lastApplyUs;
    private double _lastE2eMs;
    private int _priceDecimals = 2;
    private int _quantityDecimals = 4;

    public OrderBookViewModel(IObservable<ConnectorOption?> connector, IObservable<Symbol?> symbol, ILogger logger)
    {
        // 梯子展示两侧都按价格降序：asks 顶部是最远档、底部贴近价差行；bids 顶部是最优买价
        _asks.Connect()
            .Sort(SortExpressionComparer<OrderBookRowViewModel>.Descending(r => r.Price))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out var asks)
            .Subscribe()
            .DisposeWith(_subscriptions);
        AskRows = asks;

        _bids.Connect()
            .Sort(SortExpressionComparer<OrderBookRowViewModel>.Descending(r => r.Price))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out var bids)
            .Subscribe()
            .DisposeWith(_subscriptions);
        BidRows = bids;

        var marketData = connector
            .Select(option => option?.Connector as IMarketData)
            .Where(md => md is not null)
            .Select(md => md!)
            .DistinctUntilChanged();

        marketData.CombineLatest(symbol, (md, s) => (md, s))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Do(t =>
            {
                ClearLadder();
                _ = LoadInstrumentAsync(t.md, t.s);
            })
            .Where(t => t.s is not null)
            .Select(t =>
            {
                // 每次换订阅新建盘口实例由闭包捕获：切换天然丢弃旧盘口，无跨订阅共享状态
                var book = new LocalOrderBook();
                return t.md.SubscribeOrderBook(t.s!)
                    .Do(delta => ApplyDelta(book, delta)) // 后台线程：维护盘口 + 埋点
                    .Sample(BookThrottle)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Do(_ => RefreshLadder(book));
            })
            .Switch()
            .Subscribe(_ => { }, ex => logger.Error(ex, "Order book stream faulted"))
            .DisposeWith(_subscriptions);

        // delta 速率：1s 滚动窗口计数，UI 线程定时取样
        Observable.Interval(TimeSpan.FromSeconds(1), RxApp.MainThreadScheduler)
            .Subscribe(
                _ =>
                {
                    var rate = Interlocked.Exchange(ref _deltaCount, 0);
                    PerfText = $"delta {rate}/s · apply {_lastApplyUs:F1} µs · e2e {_lastE2eMs:F1} ms（含时钟偏移）";
                },
                ex => logger.Error(ex, "Order book perf sampler faulted"))
            .DisposeWith(_subscriptions);
    }

    public ReadOnlyObservableCollection<OrderBookRowViewModel> AskRows { get; }

    public ReadOnlyObservableCollection<OrderBookRowViewModel> BidRows { get; }

    private string _spreadText = "盘口加载中…";
    public string SpreadText
    {
        get => _spreadText;
        private set => this.RaiseAndSetIfChanged(ref _spreadText, value);
    }

    private string _lastPrice = "—";
    public string LastPrice
    {
        get => _lastPrice;
        private set => this.RaiseAndSetIfChanged(ref _lastPrice, value);
    }

    private IBrush _lastPriceBrush = Brushes.Gray;
    public IBrush LastPriceBrush
    {
        get => _lastPriceBrush;
        private set => this.RaiseAndSetIfChanged(ref _lastPriceBrush, value);
    }

    private decimal _lastPriceValue;

    private string _perfText = string.Empty;
    public string PerfText
    {
        get => _perfText;
        private set => this.RaiseAndSetIfChanged(ref _perfText, value);
    }

    private async Task LoadInstrumentAsync(IMarketData marketData, Symbol? symbol)
    {
        _priceDecimals = 2;
        _quantityDecimals = 4;
        if (symbol is null)
            return;

        try
        {
            var instruments = await marketData.GetInstrumentsAsync(symbol.Product, CancellationToken.None);
            var instrument = instruments.FirstOrDefault(i => i.Symbol.Raw == symbol.Raw);
            if (instrument is not null)
            {
                _priceDecimals = GetDecimalPlaces(instrument.TickSize);
                _quantityDecimals = GetDecimalPlaces(instrument.StepSize);
            }
        }
        catch (Exception ex)
        {
            // 精度回退到默认值，不影响盘口订阅
            Debug.WriteLine($"Failed to load instrument for {symbol.Raw}: {ex.Message}");
        }
    }

    private static int GetDecimalPlaces(decimal value)
    {
        if (value == 0) return 0;
        var s = value.ToString("G29").TrimEnd('0');
        var idx = s.IndexOf('.');
        return idx < 0 ? 0 : s.Length - idx - 1;
    }

    private void ApplyDelta(LocalOrderBook book, OrderBookDelta delta)
    {
        MarketDataMetrics.RecordDeltaReceived();
        Interlocked.Increment(ref _deltaCount);

        var start = Stopwatch.GetTimestamp();
        book.Apply(delta);
        var applyUs = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        // 端到端口径见 MarketDataMetrics 注释：交易所时间戳与本地时钟有偏移，仅供趋势观察
        var e2eMs = (DateTimeOffset.UtcNow - delta.Timestamp).TotalMilliseconds;
        MarketDataMetrics.RecordDeltaApplied(applyUs, e2eMs);

        // 后台线程写、UI 线程每秒读一次的展示值，无一致性要求，不加锁
        _lastApplyUs = applyUs;
        _lastE2eMs = e2eMs;
    }

    // UI 线程执行。10 档规模下 Clear + 灌入不是整表重建——
    // 该禁令针对的是 delta 流每次都全量重拉盘口；这里只是节流后把 Top N 投影到绑定缓存
    private void RefreshLadder(LocalOrderBook book)
    {
        var askLevels = book.GetTop(OrderSide.Sell, Depth).ToList();
        var bidLevels = book.GetTop(OrderSide.Buy, Depth).ToList();

        // asks 列表顶部高价、底部低价（最优卖）。累计量从底部最优卖向上累加，
        // 使远端高价（顶部）累计最大、深度条最长。
        var askCum = new decimal[askLevels.Count];
        decimal askSum = 0;
        for (int i = 0; i < askLevels.Count; i++)
        {
            askSum += askLevels[i].Quantity;
            askCum[i] = askSum;
        }

        // bids 列表顶部高价（最优买）、底部低价。累计量从顶部最优买向下累加，
        // 使远端低价（底部）累计最大、深度条最长。
        var bidCum = new decimal[bidLevels.Count];
        decimal bidSum = 0;
        for (int i = 0; i < bidLevels.Count; i++)
        {
            bidSum += bidLevels[i].Quantity;
            bidCum[i] = bidSum;
        }

        decimal maxCumulative = 0;
        if (askCum.Length > 0) maxCumulative = Math.Max(maxCumulative, askCum.Max());
        if (bidCum.Length > 0) maxCumulative = Math.Max(maxCumulative, bidCum.Max());
        if (maxCumulative <= 0) maxCumulative = 1;

        _asks.Edit(u =>
        {
            u.Clear();
            for (int i = 0; i < askLevels.Count; i++)
            {
                var l = askLevels[i];
                u.AddOrUpdate(new OrderBookRowViewModel
                {
                    Price = l.Price,
                    Quantity = l.Quantity,
                    CumulativeQuantity = askCum[i],
                    DepthPercent = (double)(askCum[i] / maxCumulative) * 100.0,
                    PriceBrush = Brushes.Crimson,
                    PriceDecimals = _priceDecimals,
                    QuantityDecimals = _quantityDecimals,
                });
            }
        });

        _bids.Edit(u =>
        {
            u.Clear();
            for (int i = 0; i < bidLevels.Count; i++)
            {
                var l = bidLevels[i];
                u.AddOrUpdate(new OrderBookRowViewModel
                {
                    Price = l.Price,
                    Quantity = l.Quantity,
                    CumulativeQuantity = bidCum[i],
                    DepthPercent = (double)(bidCum[i] / maxCumulative) * 100.0,
                    PriceBrush = Brushes.ForestGreen,
                    PriceDecimals = _priceDecimals,
                    QuantityDecimals = _quantityDecimals,
                });
            }
        });

        if (book is { BestBid: { } bid, BestAsk: { } ask })
        {
            var mid = (bid.Price + ask.Price) / 2m;
            SpreadText = (ask.Price - bid.Price).ToString($"F{_priceDecimals}");
            LastPriceBrush = mid >= _lastPriceValue ? Brushes.ForestGreen : Brushes.Crimson;
            _lastPriceValue = mid;
            LastPrice = mid.ToString($"F{_priceDecimals}");
        }
        else
        {
            SpreadText = "盘口加载中…";
            LastPrice = "—";
            LastPriceBrush = Brushes.Gray;
        }
    }

    private void ClearLadder()
    {
        _asks.Clear();
        _bids.Clear();
        SpreadText = "盘口加载中…";
        LastPrice = "—";
        LastPriceBrush = Brushes.Gray;
        _lastPriceValue = 0m;
    }

    public void Dispose() => _subscriptions.Dispose();
}
