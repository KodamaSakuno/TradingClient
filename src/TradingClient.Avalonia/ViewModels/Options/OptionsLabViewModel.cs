using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;
using Serilog;
using TradingClient.Application.UseCases.Options;

namespace TradingClient.Avalonia.ViewModels.Options;

/// <summary>
/// 期权实验室（§12）：T 型报价 + 波动率微笑 + 持仓 Greeks 汇总，全本地 mock，不接交易所。
/// 不进 DI 容器：生命周期跟随顶部选择器，由 MainWindowViewModel 在选中「本地 · 期权」时创建、切换时释放。
/// 参数改动去抖 300ms 后在后台线程重算（BAW 批量定价 + bump Greeks + IV 往返），算完切回 UI 线程刷新（§8.1）。
/// </summary>
public sealed class OptionsLabViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan RecalcDebounce = TimeSpan.FromMilliseconds(300);

    // 微笑画布固定尺寸。手绘 Polyline 而不引 LiveCharts2/ScottPlot：一条曲线不值一个包；
    // 坐标轴留白、不画全刻度——演示形态，曲面拟合与坐标轴刻度同属增强项
    private const double SmileWidth = 440;
    private const double SmileHeight = 180;
    private const double SmilePadding = 26;
    private const double AtmMarkerRadius = 4;

    private readonly OptionChainAnalytics _analytics;
    private readonly CompositeDisposable _subscriptions = new();

    public OptionsLabViewModel(OptionChainAnalytics analytics, ILogger logger)
    {
        _analytics = analytics;

        this.WhenAnyValue(
                vm => vm.ForwardText, vm => vm.RateText, vm => vm.AtmVolText,
                vm => vm.SmileSkewText, vm => vm.SmileCurvatureText,
                vm => vm.StrikeCountText, vm => vm.SelectedTenor)
            .Throttle(RecalcDebounce)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Select(_ => ParseInputs())
            .Do(parsed => ParamMessage = parsed is null
                ? "参数非法：F/σ₀/档距需为正，利率非负，档位数 ≥ 3"
                : string.Empty)
            .Where(parsed => parsed is not null)
            .Select(parsed => Observable.Start(() => ComputeAll(parsed!), RxApp.TaskpoolScheduler))
            .Switch()
            .ObserveOn(RxApp.MainThreadScheduler)
            // 硬故障也必须上 UI：onError 后流即终止，只写日志会让面板静默无数据
            .Subscribe(Apply, ex =>
            {
                logger.Error(ex, "Options lab recalc faulted");
                Dispatcher.UIThread.Post(() => ParamMessage = "重算失败：" + ex.Message);
            })
            .DisposeWith(_subscriptions);
    }

    public IReadOnlyList<TenorOption> Tenors { get; } =
        [new("1 个月", 1), new("2 个月", 2), new("3 个月", 3), new("6 个月", 6)];

    // 演示默认值（豆粕风格）来自 Application 层的 OptionLabDemo，注释标明演示数据
    private string _forwardText = OptionLabDemo.Forward.ToString("F0", CultureInfo.InvariantCulture);
    public string ForwardText
    {
        get => _forwardText;
        set => this.RaiseAndSetIfChanged(ref _forwardText, value);
    }

    private string _rateText = OptionLabDemo.Rate.ToString(CultureInfo.InvariantCulture);
    public string RateText
    {
        get => _rateText;
        set => this.RaiseAndSetIfChanged(ref _rateText, value);
    }

    private string _atmVolText = OptionLabDemo.AtmVol.ToString(CultureInfo.InvariantCulture);
    public string AtmVolText
    {
        get => _atmVolText;
        set => this.RaiseAndSetIfChanged(ref _atmVolText, value);
    }

    private string _smileSkewText = OptionLabDemo.SmileSkew.ToString(CultureInfo.InvariantCulture);
    public string SmileSkewText
    {
        get => _smileSkewText;
        set => this.RaiseAndSetIfChanged(ref _smileSkewText, value);
    }

    private string _smileCurvatureText = OptionLabDemo.SmileCurvature.ToString(CultureInfo.InvariantCulture);
    public string SmileCurvatureText
    {
        get => _smileCurvatureText;
        set => this.RaiseAndSetIfChanged(ref _smileCurvatureText, value);
    }

    private string _strikeCountText = OptionLabDemo.StrikeCount.ToString(CultureInfo.InvariantCulture);
    public string StrikeCountText
    {
        get => _strikeCountText;
        set => this.RaiseAndSetIfChanged(ref _strikeCountText, value);
    }

    private TenorOption? _selectedTenor;
    public TenorOption? SelectedTenor
    {
        get => _selectedTenor ??= Tenors[2];
        set => this.RaiseAndSetIfChanged(ref _selectedTenor, value);
    }

    private string _paramMessage = string.Empty;
    public string ParamMessage
    {
        get => _paramMessage;
        private set => this.RaiseAndSetIfChanged(ref _paramMessage, value);
    }

    public ObservableCollection<TQuoteRowViewModel> Rows { get; } = new();

    public ObservableCollection<PositionGreeksRowViewModel> PositionRows { get; } = new();

    private Points _smilePoints = [];
    public Points SmilePoints
    {
        get => _smilePoints;
        private set => this.RaiseAndSetIfChanged(ref _smilePoints, value);
    }

    private bool _hasSmile;
    public bool HasSmile
    {
        get => _hasSmile;
        private set => this.RaiseAndSetIfChanged(ref _hasSmile, value);
    }

    private double _atmX;
    public double AtmX
    {
        get => _atmX;
        private set => this.RaiseAndSetIfChanged(ref _atmX, value);
    }

    private double _atmY;
    public double AtmY
    {
        get => _atmY;
        private set => this.RaiseAndSetIfChanged(ref _atmY, value);
    }

    private string _smileMinVolText = string.Empty;
    public string SmileMinVolText
    {
        get => _smileMinVolText;
        private set => this.RaiseAndSetIfChanged(ref _smileMinVolText, value);
    }

    private string _smileMaxVolText = string.Empty;
    public string SmileMaxVolText
    {
        get => _smileMaxVolText;
        private set => this.RaiseAndSetIfChanged(ref _smileMaxVolText, value);
    }

    private string _totalsText = string.Empty;
    public string TotalsText
    {
        get => _totalsText;
        private set => this.RaiseAndSetIfChanged(ref _totalsText, value);
    }

    private string _hedgeText = string.Empty;
    public string HedgeText
    {
        get => _hedgeText;
        private set => this.RaiseAndSetIfChanged(ref _hedgeText, value);
    }

    private OptionChainRequest? ParseInputs()
    {
        if (!double.TryParse(ForwardText, CultureInfo.InvariantCulture, out double f) || f <= 0)
            return null;
        if (!double.TryParse(RateText, CultureInfo.InvariantCulture, out double r) || r < 0)
            return null;
        if (!double.TryParse(AtmVolText, CultureInfo.InvariantCulture, out double atmVol) || atmVol <= 0)
            return null;
        if (!double.TryParse(SmileSkewText, CultureInfo.InvariantCulture, out double skew))
            return null;
        if (!double.TryParse(SmileCurvatureText, CultureInfo.InvariantCulture, out double curvature))
            return null;
        if (!int.TryParse(StrikeCountText, CultureInfo.InvariantCulture, out int count) || count < 3)
            return null;

        var today = DateOnly.FromDateTime(DateTime.Today);
        return new OptionChainRequest(
            f, r, new SmileParameters(atmVol, skew, curvature),
            today, today.AddMonths((SelectedTenor ?? Tenors[2]).Months),
            count % 2 == 0 ? count + 1 : count, // 偶数档进一为奇数档：保证存在正中央平值档
            OptionLabDemo.StrikeStep);
    }

    // 后台线程只做纯计算（Points 是普通集合，可跨线程）；行 VM 含画刷（AvaloniaObject 有线程亲和，
    // 后台线程 new SolidColorBrush 会在 ctor 的 VerifyAccess 抛），必须在 UI 线程的 Apply 中构建
    private OptionsLabSnapshot ComputeAll(OptionChainRequest request)
    {
        var chain = _analytics.BuildChain(request);
        double atmStrike = chain.Count > 0
            ? chain.MinBy(row => Math.Abs(row.LogMoneyness))!.Strike
            : Math.Round(request.Forward / request.StrikeStep) * request.StrikeStep;
        var smile = BuildSmile(chain, request.Smile);
        var summary = _analytics.Summarize(
            OptionLabDemo.Positions(request.Expiry), request, OptionLabDemo.FuturesMultiplier);
        return new OptionsLabSnapshot(chain, request.Forward, atmStrike, smile, summary);
    }

    private static SmileCurve BuildSmile(IReadOnlyList<OptionQuoteRow> chain, SmileParameters smile)
    {
        if (chain.Count == 0)
            return new SmileCurve([], 0, 0, 0, 0);

        double mMin = chain.Min(row => row.LogMoneyness);
        double mMax = chain.Max(row => row.LogMoneyness);
        double vMin = chain.Min(row => row.InputVol);
        double vMax = chain.Max(row => row.InputVol);
        if (vMax - vMin < 1e-9)
        {
            // 微笑参数全零时曲线是平直线：纵轴零量程会除零，向两侧各让 0.5 个波动率百分点
            vMin -= 0.005;
            vMax += 0.005;
        }

        double X(double m) => SmilePadding + (m - mMin) / (mMax - mMin) * (SmileWidth - 2 * SmilePadding);
        double Y(double v) => SmilePadding + (1 - (v - vMin) / (vMax - vMin)) * (SmileHeight - 2 * SmilePadding);

        var points = new Points(chain.Select(row => new Point(X(row.LogMoneyness), Y(row.InputVol))));
        return new SmileCurve(points, X(0), Y(smile.Vol(0)), vMin, vMax);
    }

    private void Apply(OptionsLabSnapshot snapshot)
    {
        Rows.Clear();
        foreach (var row in snapshot.Chain)
            Rows.Add(new TQuoteRowViewModel(row, snapshot.Forward, snapshot.AtmStrike));

        SmilePoints = snapshot.Smile.Points;
        HasSmile = snapshot.Smile.Points.Count > 0;
        AtmX = snapshot.Smile.AtmX - AtmMarkerRadius;
        AtmY = snapshot.Smile.AtmY - AtmMarkerRadius;
        SmileMinVolText = $"σ {snapshot.Smile.MinVol:P1}";
        SmileMaxVolText = $"σ {snapshot.Smile.MaxVol:P1}";

        PositionRows.Clear();
        foreach (var row in snapshot.Summary.Rows)
            PositionRows.Add(new PositionGreeksRowViewModel(row));

        var totals = snapshot.Summary.Totals;
        TotalsText = $"合计：Δ {totals.Delta:F2} · Γ {totals.Gamma:F4} · Vega {totals.Vega:F1} · Θ {totals.Theta:F1}（数量口径：吨）";
        HedgeText = snapshot.Summary.Hedge.Text;
    }

    public void Dispose() => _subscriptions.Dispose();

    private sealed record OptionsLabSnapshot(
        IReadOnlyList<OptionQuoteRow> Chain, double Forward, double AtmStrike,
        SmileCurve Smile, OptionPortfolioSummary Summary);

    private sealed record SmileCurve(Points Points, double AtmX, double AtmY, double MinVol, double MaxVol);
}

/// <summary>到期期限预设：估值日 + N 个月。</summary>
public sealed record TenorOption(string Label, int Months)
{
    public override string ToString() => Label;
}
