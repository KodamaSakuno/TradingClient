using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Media;
using ReactiveUI;
using Serilog;
using TradingClient.Application.Abstractions;
using TradingClient.Application.UseCases.Futures;
using TradingClient.Application.UseCases.Spot;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Avalonia.ViewModels.Shared;

/// <summary>
/// 下单票（§8.2 Shared：现货/合约共用）。MainWindowViewModel 持有单个实例，
/// 目标 Symbol / 连接器跟随顶部选择器与交易对输入，按选中项的产品线分派用例。
/// 多连接器分派欠账（与 App.axaml.cs 的门面注释同源）：用例实例由 DI 绑定到 Gate，
/// 选中连接器不是对应门面实例时提示未接入、不发单，避免把单下错交易所。
/// VM 只做最薄校验（数字可解析、数量 > 0），tick/step 对齐与精度校验在用例层（§4.2）。
/// </summary>
public sealed class OrderTicketViewModel : ViewModelBase, IDisposable
{
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
                },
                ex => logger.Error(ex, "Ticket connector stream faulted"))
            .DisposeWith(_subscriptions);
        symbol
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(s => _symbol = s, ex => logger.Error(ex, "Ticket symbol stream faulted"))
            .DisposeWith(_subscriptions);

        var submit = ReactiveCommand.CreateFromTask(SubmitAsync);
        Submit = submit;
        submit.ThrownExceptions
            .Subscribe(ex => logger.Error(ex, "Order submit faulted"))
            .DisposeWith(_subscriptions);
    }

    public IReadOnlyList<OrderSide> Sides { get; } = [OrderSide.Buy, OrderSide.Sell];
    public IReadOnlyList<OrderType> OrderTypes { get; } = [OrderType.Limit, OrderType.Market];
    // 合约单带 PositionSide（§6.4 的 ReduceOnly 推算依赖持仓快照，不依赖这里的显式标志）
    public IReadOnlyList<PositionSide> PositionSides { get; } = [PositionSide.Long, PositionSide.Short];

    private OrderSide _selectedSide = OrderSide.Buy;
    public OrderSide SelectedSide
    {
        get => _selectedSide;
        set => this.RaiseAndSetIfChanged(ref _selectedSide, value);
    }

    private OrderType _selectedType = OrderType.Limit;
    public OrderType SelectedType
    {
        get => _selectedType;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedType, value);
            this.RaisePropertyChanged(nameof(IsLimit));
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
        private set => this.RaiseAndSetIfChanged(ref _isFuturesProduct, value);
    }

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

    public ICommand Submit { get; }

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
