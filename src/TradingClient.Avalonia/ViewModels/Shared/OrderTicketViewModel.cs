using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Media;
using ReactiveUI;
using Serilog;
using TradingClient.Application.Abstractions;
using TradingClient.Application.Risk;
using TradingClient.Application.UseCases.Futures;
using TradingClient.Application.UseCases.Spot;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Avalonia.ViewModels.Shared;

/// <summary>
/// 下单票（现货/合约共用）。MainWindowViewModel 持有单个实例，
/// 目标 Symbol / 连接器跟随顶部选择器与交易对输入，按选中项的产品线分派用例。
/// 多连接器分派欠账（与 App.axaml.cs 的门面注释同源）：用例实例由 DI 绑定到 Gate，
/// 选中连接器不是对应门面实例时提示未接入、不发单，避免把单下错交易所。
/// VM 只做最薄校验（数字可解析、数量 > 0），tick/step 对齐与精度校验在用例层。
/// </summary>
public sealed class OrderTicketViewModel : ViewModelBase, IDisposable
{
    private static readonly IBrush BuyColor = new SolidColorBrush(Color.Parse("#0DBF5C"));
    private static readonly IBrush SellColor = new SolidColorBrush(Color.Parse("#FF5A67"));
    private static readonly IBrush BuyInactiveColor = new SolidColorBrush(Color.Parse("#1A4330"));
    private static readonly IBrush SellInactiveColor = new SolidColorBrush(Color.Parse("#4A2A2E"));

    private readonly PlaceSpotOrder _placeSpotOrder;
    private readonly PlaceFuturesOrder _placeFuturesOrder;
    private readonly ISpotTrading _spotFacade;
    private readonly IFuturesTrading _futuresFacade;
    private readonly CompositeDisposable _subscriptions = new();

    // 最新选中项与解析出的 Symbol：订阅即缓存到字段，提交时直接读
    private ConnectorOption? _option;
    private Symbol? _symbol;

    public OrderTicketViewModel(
        PlaceSpotOrder placeSpotOrder,
        PlaceFuturesOrder placeFuturesOrder,
        ISpotTrading spotFacade,
        IFuturesTrading futuresFacade,
        IObservable<ConnectorOption?> selectedConnector,
        IObservable<Symbol?> symbol,
        ILogger logger)
    {
        _placeSpotOrder = placeSpotOrder;
        _placeFuturesOrder = placeFuturesOrder;
        _spotFacade = spotFacade;
        _futuresFacade = futuresFacade;

        selectedConnector
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(
                option =>
                {
                    _option = option;
                    IsFuturesProduct = option?.Product == ProductKind.Futures;
                    SelectedSide = OrderSide.Buy;
                    SelectedPositionSide = PositionSide.Long;
                },
                ex => logger.Error(ex, "Ticket connector stream faulted"))
            .DisposeWith(_subscriptions);
        symbol
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(
                s =>
                {
                    _symbol = s;
                    this.RaisePropertyChanged(nameof(BuyButtonText));
                    this.RaisePropertyChanged(nameof(SellButtonText));
                },
                ex => logger.Error(ex, "Ticket symbol stream faulted"))
            .DisposeWith(_subscriptions);

        var buySubmit = ReactiveCommand.CreateFromTask(BuyAsync);
        BuySubmit = buySubmit;
        buySubmit.ThrownExceptions
            .Subscribe(ex => logger.Error(ex, "Buy submit faulted"))
            .DisposeWith(_subscriptions);

        var sellSubmit = ReactiveCommand.CreateFromTask(SellAsync);
        SellSubmit = sellSubmit;
        sellSubmit.ThrownExceptions
            .Subscribe(ex => logger.Error(ex, "Sell submit faulted"))
            .DisposeWith(_subscriptions);

        SelectLimit = CreateCommand(() => SelectedType = OrderType.Limit);
        SelectMarket = CreateCommand(() => SelectedType = OrderType.Market);
        SetBestPrice = CreateCommand(() =>
        {
            if (BestPrice > 0)
                PriceText = BestPrice.ToString("G29");
        });
        SetQuantityPercent = ReactiveCommand.Create<decimal>(percent =>
        {
            var price = SelectedType == OrderType.Limit && decimal.TryParse(PriceText, out var p) && p > 0
                ? p
                : BestPrice;
            if (price <= 0 || AvailableQuote <= 0)
                return;

            var notional = AvailableQuote * percent;
            if (IsFuturesProduct && int.TryParse(LeverageText, out var leverage) && leverage > 0)
                notional *= leverage;

            var qty = notional / price;
            QuantityText = qty.ToString("G29");
        });
    }

    public IReadOnlyList<OrderSide> Sides { get; } = [OrderSide.Buy, OrderSide.Sell];
    public IReadOnlyList<OrderType> OrderTypes { get; } = [OrderType.Limit, OrderType.Market];
    // 合约单带 PositionSide（ReduceOnly 推算依赖持仓快照，不依赖这里的显式标志）
    public IReadOnlyList<PositionSide> PositionSides { get; } = [PositionSide.Long, PositionSide.Short];

    private OrderSide _selectedSide = OrderSide.Buy;
    public OrderSide SelectedSide
    {
        get => _selectedSide;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSide, value);
            this.RaisePropertyChanged(nameof(IsBuyEnabled));
            this.RaisePropertyChanged(nameof(IsSellEnabled));
        }
    }

    private OrderType _selectedType = OrderType.Limit;
    public OrderType SelectedType
    {
        get => _selectedType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedType, value);
            this.RaisePropertyChanged(nameof(IsLimit));
            this.RaisePropertyChanged(nameof(LimitButtonBackground));
            this.RaisePropertyChanged(nameof(MarketButtonBackground));
            this.RaisePropertyChanged(nameof(LimitButtonForeground));
            this.RaisePropertyChanged(nameof(MarketButtonForeground));
        }
    }

    public bool IsLimit => SelectedType == OrderType.Limit;

    private PositionSide _selectedPositionSide = PositionSide.Long;
    public PositionSide SelectedPositionSide
    {
        get => _selectedPositionSide;
        set => this.RaiseAndSetIfChanged(ref _selectedPositionSide, value);
    }

    private string _priceText = string.Empty;
    public string PriceText
    {
        get => _priceText;
        set => this.RaiseAndSetIfChanged(ref _priceText, value);
    }

    private string _quantityText = string.Empty;
    public string QuantityText
    {
        get => _quantityText;
        set => this.RaiseAndSetIfChanged(ref _quantityText, value);
    }

    private bool _isFuturesProduct;
    public bool IsFuturesProduct
    {
        get => _isFuturesProduct;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isFuturesProduct, value);
            this.RaisePropertyChanged(nameof(AvailableText));
            this.RaisePropertyChanged(nameof(MaxOpenText));
            this.RaisePropertyChanged(nameof(IsBuyEnabled));
            this.RaisePropertyChanged(nameof(IsSellEnabled));
            this.RaisePropertyChanged(nameof(BuyButtonText));
            this.RaisePropertyChanged(nameof(SellButtonText));
        }
    }

    private RiskState _riskState = RiskState.Normal;
    public RiskState RiskState
    {
        get => _riskState;
        set
        {
            this.RaiseAndSetIfChanged(ref _riskState, value);
            this.RaisePropertyChanged(nameof(IsBuyEnabled));
            this.RaisePropertyChanged(nameof(IsSellEnabled));
            this.RaisePropertyChanged(nameof(RiskHintText));
            this.RaisePropertyChanged(nameof(RiskHintBrush));
        }
    }

    private decimal _netPosition;
    public decimal NetPosition
    {
        get => _netPosition;
        set
        {
            this.RaiseAndSetIfChanged(ref _netPosition, value);
            this.RaisePropertyChanged(nameof(IsBuyEnabled));
            this.RaisePropertyChanged(nameof(IsSellEnabled));
        }
    }

    private decimal _availableQuote;
    public decimal AvailableQuote
    {
        get => _availableQuote;
        set
        {
            this.RaiseAndSetIfChanged(ref _availableQuote, value);
            this.RaisePropertyChanged(nameof(AvailableText));
            this.RaisePropertyChanged(nameof(MaxOpenText));
        }
    }

    private decimal _bestPrice;
    public decimal BestPrice
    {
        get => _bestPrice;
        set => this.RaiseAndSetIfChanged(ref _bestPrice, value);
    }

    public string LeverageText => "10"; // 占位：与 FuturesPanel 杠杆保持同步需要额外 plumbing，先写死做演示

    public string BuyButtonText => IsFuturesProduct ? "买入开多(看涨)" : "买入";
    public string SellButtonText => IsFuturesProduct ? "卖出开空(看跌)" : "卖出";
    public IBrush BuyButtonBackground => BuyColor;
    public IBrush SellButtonBackground => SellColor;

    private static readonly IBrush SelectedTypeBrush = new SolidColorBrush(Color.Parse("#E6E8EC"));
    public IBrush LimitButtonBackground => SelectedType == OrderType.Limit ? SelectedTypeBrush : Brushes.Transparent;
    public IBrush MarketButtonBackground => SelectedType == OrderType.Market ? SelectedTypeBrush : Brushes.Transparent;
    public IBrush LimitButtonForeground => SelectedType == OrderType.Limit ? Brushes.Black : Brushes.Gray;
    public IBrush MarketButtonForeground => SelectedType == OrderType.Market ? Brushes.Black : Brushes.Gray;

    public bool IsBuyEnabled =>
        RiskState != RiskState.Locked &&
        !(RiskState == RiskState.ReduceOnly && ((IsFuturesProduct && NetPosition >= 0) || !IsFuturesProduct));

    public bool IsSellEnabled =>
        RiskState != RiskState.Locked &&
        !(RiskState == RiskState.ReduceOnly && IsFuturesProduct && NetPosition <= 0);

    public string AvailableText => $"{AvailableQuote:N2} USDT";

    public string MaxOpenText
    {
        get
        {
            var price = SelectedType == OrderType.Limit && decimal.TryParse(PriceText, out var p) && p > 0
                ? p
                : BestPrice;
            if (price <= 0 || AvailableQuote <= 0)
                return "— USDT";

            var leverage = 1;
            if (IsFuturesProduct && int.TryParse(LeverageText, out var l) && l > 0)
                leverage = l;

            var max = AvailableQuote * leverage / price;
            return $"{max:G29}";
        }
    }

    public string RiskHintText => RiskState switch
    {
        RiskState.Locked => "风控锁定：禁止下单",
        RiskState.ReduceOnly => "风控 ReduceOnly：仅允许减仓方向",
        _ => string.Empty,
    };

    public IBrush RiskHintBrush => RiskState == RiskState.Locked ? Brushes.Red : Brushes.DarkOrange;

    private string _resultText = string.Empty;
    public string ResultText
    {
        get => _resultText;
        private set => this.RaiseAndSetIfChanged(ref _resultText, value);
    }

    private IBrush _resultBrush = Brushes.Gray;
    public IBrush ResultBrush
    {
        get => _resultBrush;
        private set => this.RaiseAndSetIfChanged(ref _resultBrush, value);
    }

    public ICommand BuySubmit { get; }
    public ICommand SellSubmit { get; }
    public ICommand SelectLimit { get; }
    public ICommand SelectMarket { get; }
    public ICommand SetBestPrice { get; }
    public ICommand SetQuantityPercent { get; }

    private static ICommand CreateCommand(Action action)
    {
        var cmd = ReactiveCommand.Create(action);
        cmd.ThrownExceptions.Subscribe(ex => { });
        return cmd;
    }

    private async Task BuyAsync()
    {
        SelectedSide = OrderSide.Buy;
        if (IsFuturesProduct)
            SelectedPositionSide = PositionSide.Long;
        await SubmitAsync();
    }

    private async Task SellAsync()
    {
        SelectedSide = OrderSide.Sell;
        if (IsFuturesProduct)
            SelectedPositionSide = PositionSide.Short;
        await SubmitAsync();
    }

    private async Task SubmitAsync()
    {
        if (_option is null || _symbol is null)
        {
            SetResult("请先选择交易所并输入合法交易对", isError: true);
            return;
        }
        if (!decimal.TryParse(QuantityText, out var quantity) || quantity <= 0)
        {
            SetResult("数量需为正数", isError: true);
            return;
        }
        decimal? price = null;
        if (SelectedType == OrderType.Limit)
        {
            if (!decimal.TryParse(PriceText, out var parsed) || parsed <= 0)
            {
                SetResult("限价单需填写正数价格", isError: true);
                return;
            }
            price = parsed;
        }

        switch (_option.Product)
        {
            case ProductKind.Spot:
                // 门面分派欠账：PlaceSpotOrder 绑定的是 _spotFacade（Gate），其他交易所不发单
                if (!ReferenceEquals(_option.Connector, _spotFacade))
                {
                    SetResult($"该交易所未接入现货下单（当前门面：{_spotFacade.ExchangeId}）", isError: true);
                    return;
                }
                var spot = await _placeSpotOrder.ExecuteAsync(
                    new PlaceSpotOrderRequest(_symbol, SelectedSide, SelectedType, price, quantity),
                    CancellationToken.None);
                SetResult(spot.IsSuccess
                    ? $"已下单 {spot.Value!.OrderId} · {spot.Value.Status}"
                    : $"[{spot.Error!.Code}] {spot.Error.Message}", !spot.IsSuccess);
                break;

            case ProductKind.Futures:
                if (!ReferenceEquals(_option.Connector, _futuresFacade))
                {
                    SetResult($"该交易所未接入合约下单（当前门面：{_futuresFacade.ExchangeId}）", isError: true);
                    return;
                }
                // MarginMode 固定 Cross（最小形态）；杠杆跟随连接器侧当前设置，不在票面板重复传
                var futures = await _placeFuturesOrder.ExecuteAsync(
                    new PlaceFuturesOrderRequest(
                        _symbol, SelectedSide, SelectedType, price, quantity,
                        SelectedPositionSide, MarginMode.Cross, Leverage: null),
                    CancellationToken.None);
                SetResult(futures.IsSuccess
                    ? $"已下单 {futures.Value!.OrderId} · {futures.Value.Status}"
                    : $"[{futures.Error!.Code}] {futures.Error.Message}", !futures.IsSuccess);
                break;

            default:
                SetResult("该产品线暂不支持下单", isError: true);
                break;
        }
    }

    private void SetResult(string text, bool isError)
    {
        ResultText = text;
        ResultBrush = isError ? Brushes.Red : Brushes.ForestGreen;
    }

    public void Dispose() => _subscriptions.Dispose();
}
