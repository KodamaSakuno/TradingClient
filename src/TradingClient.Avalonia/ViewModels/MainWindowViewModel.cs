using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Media;
using ReactiveUI;
using Serilog;
using TradingClient.Application.Abstractions;
using TradingClient.Application.Risk;
using TradingClient.Application.Services;
using TradingClient.Application.UseCases.Futures;
using TradingClient.Application.UseCases.Spot;
using TradingClient.Avalonia.ViewModels.Futures;
using TradingClient.Avalonia.ViewModels.Shared;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Avalonia.ViewModels;

/// <summary>
/// UI 骨架的单一 ViewModel（§8：本步最小形态，后续按 Shared/Spot/Futures 拆分）。
/// 只依赖 Application 抽象与 Domain 类型，不出现任何具体连接器类型。
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan QuoteThrottle = TimeSpan.FromMilliseconds(150); // §8.1：行情节流 100–200ms
    private static readonly TimeSpan SymbolInputDebounce = TimeSpan.FromMilliseconds(400);

    private readonly ILogger _logger;
    private readonly CompositeDisposable _subscriptions = new();

    // 交易对输入去抖后按产品线解析出的语义化 Symbol（§4.1）；Replay(1) 让后创建的合约面板立即拿到当前符号
    private readonly IObservable<Symbol?> _parsedSymbol;

    public MainWindowViewModel(
        ExchangeRegistry registry,
        PlaceSpotOrder placeSpotOrder,
        PlaceFuturesOrder placeFuturesOrder,
        ISpotTrading spotFacade,
        IFuturesTrading futuresFacade,
        RiskStateMachine riskStateMachine,
        ILogger logger)
    {
        _logger = logger;

        // 选择器条目完全由 ExchangeRegistry + Capabilities 驱动（§5.2/§8.2），不硬编码交易所名
        ConnectorOptions = registry.All
            .SelectMany(c => c.Capabilities.Products.Select(p => new ConnectorOption(c, p)))
            .ToArray();
        _selectedConnector = ConnectorOptions.FirstOrDefault();

        _parsedSymbol = this.WhenAnyValue(vm => vm.SymbolText)
            .Throttle(SymbolInputDebounce)
            .CombineLatest(
                this.WhenAnyValue(vm => vm.SelectedConnector)
                    .Select(option => option?.Product ?? ProductKind.Spot)
                    .DistinctUntilChanged(),
                ParseSymbol)
            .DistinctUntilChanged()
            .Replay(1)
            .RefCount();

        // 下单票单实例：目标跟随选择器与交易对输入，产品线分派在票内完成（§8.2 Shared）
        OrderTicket = new OrderTicketViewModel(
            placeSpotOrder, placeFuturesOrder, spotFacade, futuresFacade,
            this.WhenAnyValue(vm => vm.SelectedConnector), _parsedSymbol, logger);

        // 订单簿梯子（§8.2 Shared）：同样跟随选择器与 Symbol 流，切换由 VM 内部退订重建
        OrderBook = new OrderBookViewModel(
            this.WhenAnyValue(vm => vm.SelectedConnector), _parsedSymbol, logger);

        WireConnectionStates();
        WireQuoteStream();
        WireConnectorActivation();
        WireFuturesPanel();
        WireRiskState(riskStateMachine);
    }

    public IReadOnlyList<ConnectorOption> ConnectorOptions { get; }

    public OrderTicketViewModel OrderTicket { get; }

    public OrderBookViewModel OrderBook { get; }

    private ConnectorOption? _selectedConnector;
    public ConnectorOption? SelectedConnector
    {
        get => _selectedConnector;
        set => this.RaiseAndSetIfChanged(ref _selectedConnector, value);
    }

    // 合约面板跟随选中项生命周期，不进 DI 容器（§8.2）；非合约选中项时为 null
    private FuturesPanelViewModel? _futuresPanel;
    public FuturesPanelViewModel? FuturesPanel
    {
        get => _futuresPanel;
        private set => this.RaiseAndSetIfChanged(ref _futuresPanel, value);
    }

    private bool _isFuturesSelected;
    public bool IsFuturesSelected
    {
        get => _isFuturesSelected;
        private set => this.RaiseAndSetIfChanged(ref _isFuturesSelected, value);
    }

    private string _symbolText = "BTC_USDT";
    public string SymbolText
    {
        get => _symbolText;
        set => this.RaiseAndSetIfChanged(ref _symbolText, value);
    }

    private string _bestBid = "—";
    public string BestBid
    {
        get => _bestBid;
        private set => this.RaiseAndSetIfChanged(ref _bestBid, value);
    }

    private string _bestAsk = "—";
    public string BestAsk
    {
        get => _bestAsk;
        private set => this.RaiseAndSetIfChanged(ref _bestAsk, value);
    }

    private string _quoteTimestamp = "—";
    public string QuoteTimestamp
    {
        get => _quoteTimestamp;
        private set => this.RaiseAndSetIfChanged(ref _quoteTimestamp, value);
    }

    private string _symbolMessage = string.Empty;
    public string SymbolMessage
    {
        get => _symbolMessage;
        private set => this.RaiseAndSetIfChanged(ref _symbolMessage, value);
    }

    private string _connectionStatus = nameof(ConnectionState.Disconnected);
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => this.RaiseAndSetIfChanged(ref _connectionStatus, value);
    }

    private IBrush _connectionBrush = Brushes.Gray;
    public IBrush ConnectionBrush
    {
        get => _connectionBrush;
        private set => this.RaiseAndSetIfChanged(ref _connectionBrush, value);
    }

    private string _riskStateText = nameof(RiskState.Normal);
    public string RiskStateText
    {
        get => _riskStateText;
        private set => this.RaiseAndSetIfChanged(ref _riskStateText, value);
    }

    private IBrush _riskStateBrush = Brushes.Gray;
    public IBrush RiskStateBrush
    {
        get => _riskStateBrush;
        private set => this.RaiseAndSetIfChanged(ref _riskStateBrush, value);
    }

    private FontWeight _riskStateFontWeight = FontWeight.Normal;
    public FontWeight RiskStateFontWeight
    {
        get => _riskStateFontWeight;
        private set => this.RaiseAndSetIfChanged(ref _riskStateFontWeight, value);
    }

    private bool _hasRiskBanner;
    public bool HasRiskBanner
    {
        get => _hasRiskBanner;
        private set => this.RaiseAndSetIfChanged(ref _hasRiskBanner, value);
    }

    private string _riskBannerText = string.Empty;
    public string RiskBannerText
    {
        get => _riskBannerText;
        private set => this.RaiseAndSetIfChanged(ref _riskBannerText, value);
    }

    public ObservableCollection<AssetBalance> Balances { get; } = new();

    private string _accountSummary = "余额未加载";
    public string AccountSummary
    {
        get => _accountSummary;
        private set => this.RaiseAndSetIfChanged(ref _accountSummary, value);
    }

    // 选中项变化（含订阅时的初始值）即激活该连接器：连接 + 清空旧余额 + 拉新余额
    private void WireConnectorActivation()
    {
        this.WhenAnyValue(vm => vm.SelectedConnector)
            .Where(option => option is not null)
            .DistinctUntilChanged()
            .Subscribe(
                option => _ = ActivateConnectorAsync(option!.Connector),
                ex => _logger.Error(ex, "Connector activation stream faulted"))
            .DisposeWith(_subscriptions);
    }

    private async Task ActivateConnectorAsync(IExchangeConnector connector)
    {
        try
        {
            // ConnectAsync 重复调用是幂等的校时 + 状态推进，对切换回来的连接器无害
            await connector.ConnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 连接失败视为系统故障：记录日志并降级，行情订阅由适配器的重连机制兜底（§7）
            _logger.Error(ex, "Failed to connect exchange {ExchangeId}", connector.ExchangeId);
        }

        // 切换交易所先清空旧账户展示，避免新旧余额混排
        Balances.Clear();
        if (connector is IAccountService account)
            await LoadAccountAsync(connector.ExchangeId, account);
    }

    private async Task LoadAccountAsync(string exchangeId, IAccountService account)
    {
        var result = await account.GetAccountAsync(CancellationToken.None);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            // 环境变量名是组装细节（Composition Root 的职责），VM 只按交易所给通用提示
            AccountSummary = error.Code == "MISSING_CREDENTIALS"
                ? $"未配置 {exchangeId} 凭证（设置环境变量后重启）"
                : $"余额加载失败：[{error.Code}] {error.Message}";
            _logger.Warning("Account load failed: [{ErrorCode}] {ErrorMessage}", error.Code, error.Message);
            return;
        }

        Balances.Clear();
        foreach (var asset in result.Value!.Assets)
            Balances.Add(asset);
        AccountSummary = $"账户模式：{result.Value.Mode} · {result.Value.Assets.Count} 个币种";
    }

    // 连接状态流 → 状态文本/颜色；推送在后台线程，ObserveOn 在 ViewModel 边界切回 UI 线程（§8.1）
    private void WireConnectionStates()
    {
        this.WhenAnyValue(vm => vm.SelectedConnector)
            .Where(option => option is not null)
            .Select(option => option!.Connector.ConnectionStates.ObserveOn(RxApp.MainThreadScheduler))
            .Switch()
            .Subscribe(
                state =>
                {
                    ConnectionStatus = state.ToString();
                    ConnectionBrush = state switch
                    {
                        ConnectionState.Connected => Brushes.ForestGreen,
                        ConnectionState.Connecting or ConnectionState.Reconnecting => Brushes.DarkOrange,
                        _ => Brushes.Gray,
                    };
                },
                ex => _logger.Error(ex, "Connection state stream faulted"))
            .DisposeWith(_subscriptions);
    }

    // 合约面板跟随选中项：选中「合约」且连接器实现 IFuturesTrading 时创建，切换即释放重建
    // （重建天然完成清空持仓、消息、预警并退订全部流的要求）
    private void WireFuturesPanel()
    {
        this.WhenAnyValue(vm => vm.SelectedConnector)
            .Subscribe(
                option =>
                {
                    FuturesPanel?.Dispose();
                    FuturesPanel = option is { Product: ProductKind.Futures } && option.Connector is IFuturesTrading futures
                        ? new FuturesPanelViewModel(futures, _parsedSymbol, _logger)
                        : null;
                    IsFuturesSelected = FuturesPanel is not null;
                },
                ex => _logger.Error(ex, "Futures panel stream faulted"))
            .DisposeWith(_subscriptions);
    }

    // 行情管道：Symbol 输入去抖 → 按产品线解析为语义化 Symbol（§4.1）→ 换订阅（Switch 自动退订旧流）
    // → Sample 节流 → ViewModel 边界 ObserveOn 切 UI 线程（§8.1）
    // IMarketData.SubscribeQuotes 签名是 Symbol，适配器按子类型路由到现货/期货频道
    private void WireQuoteStream()
    {
        var marketData = this.WhenAnyValue(vm => vm.SelectedConnector)
            .Select(option => option?.Connector as IMarketData)
            .Where(md => md is not null)
            .Select(md => md!)
            .DistinctUntilChanged();

        marketData.CombineLatest(_parsedSymbol, (md, s) => (md, s))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Do(t => SymbolMessage = t.s is null ? "无法解析交易对，格式如 BTC_USDT" : string.Empty)
            .Where(t => t.s is not null)
            .Select(t => t.md.SubscribeQuotes(t.s!)
                .Sample(QuoteThrottle)
                .ObserveOn(RxApp.MainThreadScheduler))
            .Switch()
            .Subscribe(
                quote =>
                {
                    BestBid = quote.BestBid.ToString("G29");
                    BestAsk = quote.BestAsk.ToString("G29");
                    QuoteTimestamp = quote.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
                },
                ex => _logger.Error(ex, "Quote stream faulted"))
            .DisposeWith(_subscriptions);
    }

    // 风控状态机（§6.4）：常驻显示当前状态；迁移横幅只在 ReduceOnly/Locked 停留展示，回到 Normal/Warning 收起
    private void WireRiskState(RiskStateMachine stateMachine)
    {
        ApplyRiskState(stateMachine.Current);
        stateMachine.Transitions
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(
                t =>
                {
                    ApplyRiskState(t.To);
                    RiskBannerText = $"{t.Timestamp.ToLocalTime():HH:mm:ss} 风控 {t.From} → {t.To}：{t.Reason}";
                    HasRiskBanner = t.To is RiskState.ReduceOnly or RiskState.Locked;
                },
                ex => _logger.Error(ex, "Risk transition stream faulted"))
            .DisposeWith(_subscriptions);
    }

    private void ApplyRiskState(RiskState state)
    {
        RiskStateText = state.ToString();
        (RiskStateBrush, RiskStateFontWeight) = state switch
        {
            RiskState.Warning => (Brushes.DarkOrange, FontWeight.Normal),
            RiskState.ReduceOnly => (Brushes.DarkOrange, FontWeight.Bold),
            RiskState.Locked => (Brushes.Red, FontWeight.Bold),
            _ => (Brushes.Gray, FontWeight.Normal),
        };
    }

    // 输入格式固定为 Base_Quote 两段：现货 → SpotSymbol，合约 → PerpetualFuturesSymbol
    private static Symbol? ParseSymbol(string text, ProductKind product)
    {
        var parts = text.Replace('/', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return null;
        var baseAsset = parts[0].ToUpperInvariant();
        var quoteAsset = parts[1].ToUpperInvariant();
        return product == ProductKind.Futures
            ? new PerpetualFuturesSymbol(baseAsset, quoteAsset)
            : new SpotSymbol(baseAsset, quoteAsset);
    }

    public void Dispose()
    {
        FuturesPanel?.Dispose();
        OrderTicket.Dispose();
        OrderBook.Dispose();
        _subscriptions.Dispose();
    }
}

/// <summary>
/// 顶部选择器条目：一个连接器 × 一条产品线。显示名由 Capabilities 推导，禁止硬编码交易所名（§5.2）。
/// </summary>
public sealed record ConnectorOption(IExchangeConnector Connector, ProductKind Product)
{
    public string DisplayName => $"{Connector.ExchangeId} · {ProductLabel}";

    private string ProductLabel => Product switch
    {
        ProductKind.Spot => "现货",
        ProductKind.Futures => "合约",
        ProductKind.Options => "期权",
        _ => Product.ToString(),
    };
}
