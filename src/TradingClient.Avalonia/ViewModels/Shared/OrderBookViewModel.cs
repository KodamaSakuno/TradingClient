using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
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
/// 订单簿梯子（§8.2：现货/合约共用）。由 MainWindowViewModel 持有，跟随顶部选择器与 Symbol 流，
/// 切换即退订旧流、丢弃旧盘口（新建 LocalOrderBook）重建。
/// 管道：SubscribeOrderBook → 后台线程 Apply 进本地盘口（埋点计时）→ Sample 150ms（§8.1）
/// → ObserveOn UI 线程 → 两侧各 10 档灌进 SourceCache 供绑定。
/// </summary>
public sealed class OrderBookViewModel : ViewModelBase, IDisposable
{
    private const int Depth = 10;
    private static readonly TimeSpan BookThrottle = TimeSpan.FromMilliseconds(150); // §8.1：行情节流 100–200ms

    private readonly CompositeDisposable _subscriptions = new();
    private readonly SourceCache<OrderBookLevel, decimal> _bids = new(l => l.Price);
    private readonly SourceCache<OrderBookLevel, decimal> _asks = new(l => l.Price);

    private int _deltaCount; // 后台线程 Interlocked 递增，UI 线程每秒取样清零
    private double _lastApplyUs;
    private double _lastE2eMs;

    public OrderBookViewModel(IObservable<ConnectorOption?> connector, IObservable<Symbol?> symbol, ILogger logger)
    {
        // 梯子展示两侧都按价格降序：asks 顶部是最远档、底部贴近价差行；bids 顶部是最优买价
        _asks.Connect()
            .Sort(SortExpressionComparer<OrderBookLevel>.Descending(l => l.Price))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out var asks)
            .Subscribe()
            .DisposeWith(_subscriptions);
        Asks = asks;

        _bids.Connect()
            .Sort(SortExpressionComparer<OrderBookLevel>.Descending(l => l.Price))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out var bids)
            .Subscribe()
            .DisposeWith(_subscriptions);
        Bids = bids;

        var marketData = connector
            .Select(option => option?.Connector as IMarketData)
            .Where(md => md is not null)
            .Select(md => md!)
            .DistinctUntilChanged();

        marketData.CombineLatest(symbol, (md, s) => (md, s))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Do(_ => ClearLadder()) // 切换（含 Symbol 变 null）即清空旧档位展示
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

    public ReadOnlyObservableCollection<OrderBookLevel> Asks { get; }

    public ReadOnlyObservableCollection<OrderBookLevel> Bids { get; }

    private string _spreadText = "盘口加载中…";
    public string SpreadText
    {
        get => _spreadText;
        private set => this.RaiseAndSetIfChanged(ref _spreadText, value);
    }

    private string _perfText = string.Empty;
    public string PerfText
    {
        get => _perfText;
        private set => this.RaiseAndSetIfChanged(ref _perfText, value);
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

    // UI 线程执行。10 档规模下 Clear + 灌入不算 §8.1 禁的"整表重建"——
    // 该禁令针对的是 delta 流每次都全量重拉盘口；这里只是节流后把 Top N 投影到绑定缓存
    private void RefreshLadder(LocalOrderBook book)
    {
        _asks.Edit(u =>
        {
            u.Clear();
            u.AddOrUpdate(book.GetTop(OrderSide.Sell, Depth));
        });
        _bids.Edit(u =>
        {
            u.Clear();
            u.AddOrUpdate(book.GetTop(OrderSide.Buy, Depth));
        });

        SpreadText = book is { BestBid: { } bid, BestAsk: { } ask }
            ? $"价差 {(ask.Price - bid.Price):G29} · 买 {bid.Price:G29} / 卖 {ask.Price:G29}"
            : "盘口加载中…";
    }

    private void ClearLadder()
    {
        _asks.Clear();
        _bids.Clear();
        SpreadText = "盘口加载中…";
    }

    public void Dispose() => _subscriptions.Dispose();
}
