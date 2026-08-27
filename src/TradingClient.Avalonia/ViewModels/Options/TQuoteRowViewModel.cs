using Avalonia.Media;
using TradingClient.Application.UseCases.Options;

namespace TradingClient.Avalonia.ViewModels.Options;

/// <summary>
/// T 型报价行 VM：Call 侧 | 行权价 | Put 侧镜像。文本在构建时一次格式化；
/// 含画刷（AvaloniaObject 线程亲和），只能在 UI 线程构建——由 Apply 负责，禁止挪进后台计算。
/// </summary>
public sealed class TQuoteRowViewModel
{
    private static readonly IBrush ItmCallBrush = new SolidColorBrush(Color.Parse("#E8F0FE"));
    private static readonly IBrush ItmPutBrush = new SolidColorBrush(Color.Parse("#FDEDEC"));

    public TQuoteRowViewModel(OptionQuoteRow row, double forward, double atmStrike)
    {
        StrikeText = row.Strike.ToString("F0");
        StrikeWeight = row.Strike == atmStrike ? FontWeight.Bold : FontWeight.Normal;
        CallBackground = row.Strike < forward ? ItmCallBrush : null;
        PutBackground = row.Strike > forward ? ItmPutBrush : null;

        CallIvText = FormatIv(row.CallIv);
        CallDeltaText = row.CallGreeks.Delta.ToString("F4");
        CallGammaText = row.CallGreeks.Gamma.ToString("F6");
        CallVegaText = row.CallGreeks.Vega.ToString("F2");
        CallThetaText = row.CallGreeks.Theta.ToString("F2");
        CallTheoText = row.CallTheo.ToString("F2");

        PutTheoText = row.PutTheo.ToString("F2");
        PutThetaText = row.PutGreeks.Theta.ToString("F2");
        PutVegaText = row.PutGreeks.Vega.ToString("F2");
        PutGammaText = row.PutGreeks.Gamma.ToString("F6");
        PutDeltaText = row.PutGreeks.Delta.ToString("F4");
        PutIvText = FormatIv(row.PutIv);
    }

    // IV 反解失败（错误码见 ImpliedVolatility 注释）显示 "—"，不炸面板
    private static string FormatIv(double? iv) => iv is { } value ? value.ToString("P1") : "—";

    public string StrikeText { get; }
    public FontWeight StrikeWeight { get; }
    public IBrush? CallBackground { get; }
    public IBrush? PutBackground { get; }

    public string CallIvText { get; }
    public string CallDeltaText { get; }
    public string CallGammaText { get; }
    public string CallVegaText { get; }
    public string CallThetaText { get; }
    public string CallTheoText { get; }

    public string PutTheoText { get; }
    public string PutThetaText { get; }
    public string PutVegaText { get; }
    public string PutGammaText { get; }
    public string PutDeltaText { get; }
    public string PutIvText { get; }
}

/// <summary>持仓 Greeks 汇总行 VM：数量为带符号吨数，Greeks 已是 × 数量后的持仓级数值。</summary>
public sealed class PositionGreeksRowViewModel
{
    public PositionGreeksRowViewModel(PositionGreeksRow row)
    {
        SymbolText = row.Position.Symbol.Raw;
        QuantityText = row.Position.Quantity.ToString("F0");
        OpenPriceText = row.Position.OpenPrice.ToString("F1");
        DeltaText = row.Greeks.Delta.ToString("F2");
        GammaText = row.Greeks.Gamma.ToString("F4");
        VegaText = row.Greeks.Vega.ToString("F1");
        ThetaText = row.Greeks.Theta.ToString("F1");
    }

    public string SymbolText { get; }
    public string QuantityText { get; }
    public string OpenPriceText { get; }
    public string DeltaText { get; }
    public string GammaText { get; }
    public string VegaText { get; }
    public string ThetaText { get; }
}
