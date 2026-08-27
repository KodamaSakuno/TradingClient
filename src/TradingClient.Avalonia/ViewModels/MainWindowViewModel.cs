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
using TradingClient.Application.UseCases.Options;
using TradingClient.Application.UseCases.Spot;
using TradingClient.Avalonia.ViewModels.Futures;
using TradingClient.Avalonia.ViewModels.Options;
using TradingClient.Avalonia.ViewModels.Shared;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Avalonia.ViewModels;

/// <summary>
/// UI 骨架的单一 ViewModel（本步最小形态，后续按 Shared/Spot/Futures 拆分）。
/// 只依赖 Application 抽象与 Domain 类型，不出现任何具体连接器类型。
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan QuoteThrottle = TimeSpan.FromMilliseconds(150); // 行情节流 100–200ms
    private static readonly TimeSpan SymbolInputDebounce = TimeSpan.FromMilliseconds(400);

    private readonly ILogger _logger;
    private readonly OptionChainAnalytics _optionChainAnalytics;
    private readonly CompositeDisposable _subscriptions = new();

    // 交易对输入去抖后按产品线解析出的语义化 Symbol；Replay(1) 让后创建的合约面板立即拿到当前符号
    private readonly IObservable<Symbol?> _parsedSymbol;

    // 「本地 · 期权」选择器条目：本地分析模块，不是交易所能力，不走 Capabilities，在此显式追加
    private static readonly LocalModuleOption OptionsLabOption = new("本地 · 期权");

    public MainWindowViewModel(
        ExchangeRegistry registry,
        PlaceSpotOrder placeSpotOrder,
        PlaceFuturesOrder placeFuturesOrder,
        ISpotTrading spotFacade,
        IFuturesTrading futuresFacade,
        RiskStateMachine riskStateMachine,
        OptionChainAnalytics optionChainAnalytics,
        ILogger logger)
    {
        _logger = logger;
        _optionChainAnalytics = optionChainAnalytics;

        // 交易所条目完全由 ExchangeRegistry + Capabilities 驱动，不硬编码交易所名
        SelectorOptions = registry.All
            .SelectMany(c => c.Capabilities.Products.Select(p => (SelectorOption)new ConnectorOption(c, p)))
            .Append(OptionsLabOption)
            .ToArray();
        _selectedOption = SelectorOptions.FirstOrDefault();

        // 连接器形态的选择投影：本地模块条目投影为 null，下游管道（OrderTicket/OrderBook/激活/行情）天然静默
        var connectorSelection = this.WhenAnyValue(vm => vm.SelectedOption)
            .Select(option => option as ConnectorOption);

        _parsedSymbol = this.WhenAnyValue(vm => vm.SymbolText)
            .Throttle(SymbolInputDebounce)
            .CombineLatest(
                connectorSelection
                    .Select(option => option?.Product ?? ProductKind.Spot)
                    .DistinctUntilChanged(),
                ParseSymbol)
            .DistinctUntilChanged()
            .Replay(1)
            .RefCount();

        // 下单票单实例：目标跟随选择器与交易对输入，产品线分派在票内完成
        OrderTicket = new OrderTicketViewModel(
            placeSpotOrder, placeFuturesOrder, spotFacade, futuresFacade,
            connectorSelection, _parsedSymbol, logger);

        // 订单簿梯子（现货/合约共用）：同样跟随选择器与 Symbol 流，切换由 VM 内部退订重建
        OrderBook = new OrderBookViewModel(connectorSelection, _parsedSymbol, logger);

        WireConnectionStates(connectorSelection);
        WireQuoteStream(connectorSelection);
        WireConnectorActivation(connectorSelection);
        WireFuturesPanel(connectorSelection);
        WireOptionsLab();
        WireRiskState(riskStateMachine);
    }

    public IReadOnlyList<SelectorOption> SelectorOptions { get; }

    public OrderTicketViewModel OrderTicket { get; }

    public OrderBookViewModel OrderBook { get; }

    private SelectorOption? _selectedOption;
    public SelectorOption? SelectedOption
    {
        get => _selectedOption;
        set => this.RaiseAndSetIfChanged(ref _selectedOption, value);
    }

    // 合约面板跟随选中项生命周期，不进 DI 容器；非合约选中项时为 null
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

    // 期权实验室（本地模块）：与合约面板同款生命周期，跟随「本地 · 期权」选中项创建/释放
    private OptionsLabViewModel? _optionsLab;
    public OptionsLabViewModel? OptionsLab
    {
        get => _optionsLab;
        private set => this.RaiseAndSetIfChanged(ref _optionsLab, value);
    }

    private bool _isOptionsLabSelected;
    public bool IsOptionsLabSelected
    {
        get => _isOptionsLabSelected;
        private set => this.RaiseAndSetIfChanged(ref _isOptionsLabSelected, value);
    }

    // 交易所条目选中时为 true：行情/订单簿/票面/余额各区据此显隐（选中本地模块时隐藏）
    private bool _isExchangeSelected = true;
    public bool IsExchangeSelected
    {
        get => _isExchangeSelected;
        private set => this.RaiseAndSetIfChanged(ref _isExchangeSelected, value);
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
    private void WireConnectorActivation(IObservable<ConnectorOption?> connectorSelection)
    {
        connectorSelection
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
            // 连接失败视为系统故障：记录日志并降级，行情订阅由适配器的重连机制兜底
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

    // 连接状态流 → 状态文本/颜色；推送在后台线程，ObserveOn 在 ViewModel 边界切回 UI 线程
    private void WireConnectionStates(IObservable<ConnectorOption?> connectorSelection)
    {
        connectorSelection
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
    private void WireFuturesPanel(IObservable<ConnectorOption?> connectorSelection)
    {
        connectorSelection
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

    // 期权实验室跟随选中项：选中「本地 · 期权」（LocalModuleOption）时创建，切走即释放；
    // 同时维护 IsExchangeSelected 驱动交易所各区显隐
    private void WireOptionsLab()
    {
        this.WhenAnyValue(vm => vm.SelectedOption)
            .Subscribe(
                option =>
                {
                    OptionsLab?.Dispose();
                    OptionsLab = option is LocalModuleOption
                        ? new OptionsLabViewModel(_optionChainAnalytics, _logger)
                        : null;
                    IsOptionsLabSelected = OptionsLab is not null;
                    IsExchangeSelected = option is ConnectorOption;
                },
                ex => _logger.Error(ex, "Options lab stream faulted"))
            .DisposeWith(_subscriptions);
    }

    // 行情管道：Symbol 输入去抖 → 按产品线解析为语义化 Symbol → 换订阅（Switch 自动退订旧流）
    // → Sample 节流 → ViewModel 边界 ObserveOn 切 UI 线程
    // IMarketData.SubscribeQuotes 签名是 Symbol，适配器按子类型路由到现货/期货频道
    private void WireQuoteStream(IObservable<ConnectorOption?> connectorSelection)
    {
        var marketData = connectorSelection
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

    // 风控状态机：常驻显示当前状态；迁移横幅只在 ReduceOnly/Locked 停留展示，回到 Normal/Warning 收起
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
        OptionsLab?.Dispose();
        FuturesPanel?.Dispose();
        OrderTicket.Dispose();
        OrderBook.Dispose();
        _subscriptions.Dispose();
    }
}

/// <summary>
/// 顶部选择器条目的联合类型：交易所连接器 × 产品线（Capabilities 驱动），
/// 或本地分析模块（非交易所能力，不走 Capabilities）。
/// </summary>
public abstract record SelectorOption
{
    public abstract string DisplayName { get; }
}

/// <summary>
/// 选择器条目：一个连接器 × 一条产品线。显示名由 Capabilities 推导，禁止硬编码交易所名。
/// </summary>
public sealed record ConnectorOption(IExchangeConnector Connector, ProductKind Product) : SelectorOption
{
    public override string DisplayName => $"{Connector.ExchangeId} · {ProductLabel}";

    private string ProductLabel => Product switch
    {
        ProductKind.Spot => "现货",
        ProductKind.Futures => "合约",
        ProductKind.Options => "期权",
        _ => Product.ToString(),
    };
}

/// <summary>选择器条目：本地分析模块（期权实验室）。无连接器、无产品线。</summary>
public sealed record LocalModuleOption(string Label) : SelectorOption
{
    public override string DisplayName => Label;
}
