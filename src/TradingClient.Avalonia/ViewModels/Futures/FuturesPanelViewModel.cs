using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Media;
using DynamicData;
using ReactiveUI;
using Serilog;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Avalonia.ViewModels.Futures;

/// <summary>
/// 合约面板（持仓 / 杠杆 / 持仓模式 / 强平预警）。不进 DI 容器：生命周期跟随顶部选择器，
/// 由 MainWindowViewModel 在选中「合约」条目时创建、切换时释放重建（重建即清空全部状态）。
/// 只依赖 IFuturesTrading 抽象与 Domain 类型，不出现具体连接器类型。
/// </summary>
public sealed class FuturesPanelViewModel : ViewModelBase, IDisposable
{
    private readonly IFuturesTrading _connector;
    private readonly ILogger _logger;
    private readonly CompositeDisposable _subscriptions = new();

    // 键 = Symbol.Raw + Side：dual 模式下同一合约有 Long/Short 两腿
    private readonly SourceCache<Position, string> _positions = new(p => $"{p.Symbol.Raw}:{p.Side}");

    public FuturesPanelViewModel(IFuturesTrading connector, IObservable<Symbol?> symbol, ILogger logger)
    {
        _connector = connector;
        _logger = logger;

        SupportsDualPositionMode = connector.Capabilities.SupportsDualPositionMode;

        // 推送在后台线程，进缓存前不切线程；绑定时由 DynamicData 管道统一切 UI 线程（§8.1/§8.2）
        _positions.Connect()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out var positions)
            .Subscribe()
            .DisposeWith(_subscriptions);
        Positions = positions;

        // 当前合约符号（行情管道解析结果的共享流），杠杆设置的目标
        symbol.Select(s => s as PerpetualFuturesSymbol)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(s => CurrentSymbol = s, ex => logger.Error(ex, "Symbol stream faulted"))
            .DisposeWith(_subscriptions);

        var canApplyLeverage = this.WhenAnyValue(vm => vm.CurrentSymbol).Select(s => s is not null);
        var applyLeverage = ReactiveCommand.CreateFromTask(ApplyLeverageAsync, canApplyLeverage);
        var setSinglePositionMode = ReactiveCommand.CreateFromTask(() => SetPositionModeAsync(PositionMode.Single));
        var setDualPositionMode = ReactiveCommand.CreateFromTask(() => SetPositionModeAsync(PositionMode.Dual));
        ApplyLeverage = applyLeverage;
        SetSinglePositionMode = setSinglePositionMode;
        SetDualPositionMode = setDualPositionMode;
        foreach (var cmd in new IReactiveCommand[] { applyLeverage, setSinglePositionMode, setDualPositionMode })
            cmd.ThrownExceptions
                .Subscribe(ex => logger.Error(ex, "Futures command faulted"))
                .DisposeWith(_subscriptions);

        // 初始快照 + 增量流共同维护 SourceCache
        _ = LoadPositionsAsync();
        connector.PositionUpdates
            .Subscribe(u => ApplyPosition(u.Position), ex => logger.Error(ex, "PositionUpdates stream faulted"))
            .DisposeWith(_subscriptions);

        // 强平预警：保留最后一条（简单做法，不做超时淡出）
        connector.LiquidationWarnings
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(
                w =>
                {
                    LiquidationWarningText =
                        $"强平预警 {w.Symbol.Raw} {w.Side} · 估算强平价 {w.EstimatedLiquidationPrice:G29} · 保证金率 {w.MarginRatio:P1}";
                    LiquidationWarningBrush = w.MarginRatio >= 0.9m ? Brushes.Red : Brushes.DarkOrange;
                    HasLiquidationWarning = true;
                },
                ex => logger.Error(ex, "LiquidationWarnings stream faulted"))
            .DisposeWith(_subscriptions);
    }

    public bool SupportsDualPositionMode { get; }

    public ReadOnlyObservableCollection<Position> Positions { get; }

    private PerpetualFuturesSymbol? _currentSymbol;
    public PerpetualFuturesSymbol? CurrentSymbol
    {
        get => _currentSymbol;
        private set => this.RaiseAndSetIfChanged(ref _currentSymbol, value);
    }

    private string _positionsMessage = string.Empty;
    public string PositionsMessage
    {
        get => _positionsMessage;
        private set => this.RaiseAndSetIfChanged(ref _positionsMessage, value);
    }

    private string _leverageText = "10";
    public string LeverageText
    {
        get => _leverageText;
        set => this.RaiseAndSetIfChanged(ref _leverageText, value);
    }

    public IReadOnlyList<MarginMode> MarginModes { get; } = [MarginMode.Cross, MarginMode.Isolated];

    private MarginMode _selectedMarginMode = MarginMode.Cross;
    public MarginMode SelectedMarginMode
    {
        get => _selectedMarginMode;
        set => this.RaiseAndSetIfChanged(ref _selectedMarginMode, value);
    }

    private string _leverageMessage = string.Empty;
    public string LeverageMessage
    {
        get => _leverageMessage;
        private set => this.RaiseAndSetIfChanged(ref _leverageMessage, value);
    }

    // IFuturesTrading 没有持仓模式查询接口，当前模式无从得知，初始显示"未知"，设置后只反映最近一次调用结果
    private string _positionModeStatus = "当前持仓模式：未知";
    public string PositionModeStatus
    {
        get => _positionModeStatus;
        private set => this.RaiseAndSetIfChanged(ref _positionModeStatus, value);
    }

    private bool _hasLiquidationWarning;
    public bool HasLiquidationWarning
    {
        get => _hasLiquidationWarning;
        private set => this.RaiseAndSetIfChanged(ref _hasLiquidationWarning, value);
    }

    private string _liquidationWarningText = string.Empty;
    public string LiquidationWarningText
    {
        get => _liquidationWarningText;
        private set => this.RaiseAndSetIfChanged(ref _liquidationWarningText, value);
    }

    private IBrush _liquidationWarningBrush = Brushes.DarkOrange;
    public IBrush LiquidationWarningBrush
    {
        get => _liquidationWarningBrush;
        private set => this.RaiseAndSetIfChanged(ref _liquidationWarningBrush, value);
    }

    public ICommand ApplyLeverage { get; }
    public ICommand SetSinglePositionMode { get; }
    public ICommand SetDualPositionMode { get; }

    private async Task LoadPositionsAsync()
    {
        var result = await _connector.GetPositionsAsync(CancellationToken.None);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            PositionsMessage = $"持仓加载失败：[{error.Code}] {error.Message}";
            _logger.Warning("Positions load failed: [{ErrorCode}] {ErrorMessage}", error.Code, error.Message);
            return;
        }

        _positions.Edit(update =>
        {
            update.Clear();
            foreach (var p in result.Value!.Where(p => p.Quantity != 0))
                update.AddOrUpdate(p);
        });
    }

    // Quantity==0 的推送视为平仓，移除该键
    private void ApplyPosition(Position position)
    {
        if (position.Quantity == 0)
            _positions.RemoveKey($"{position.Symbol.Raw}:{position.Side}");
        else
            _positions.AddOrUpdate(position);
    }

    private async Task ApplyLeverageAsync()
    {
        if (CurrentSymbol is not { } symbol)
            return; // CanExecute 已保证，防御分支
        if (!int.TryParse(LeverageText, out var leverage) || leverage is < 1 or > 125)
        {
            LeverageMessage = "杠杆需为 1–125 的整数";
            return;
        }

        var result = await _connector.SetLeverageAsync(symbol, leverage, SelectedMarginMode, CancellationToken.None);
        LeverageMessage = result.IsSuccess
            ? $"杠杆已设置：{symbol.Raw} {leverage}x {SelectedMarginMode}"
            : $"杠杆设置失败：[{result.Error!.Code}] {result.Error.Message}";
    }

    private async Task SetPositionModeAsync(PositionMode mode)
    {
        var result = await _connector.SetPositionModeAsync(mode, CancellationToken.None);
        PositionModeStatus = result.IsSuccess
            ? $"当前持仓模式：{mode}"
            : $"持仓模式设置失败：[{result.Error!.Code}] {result.Error.Message}";
    }

    public void Dispose() => _subscriptions.Dispose();
}
