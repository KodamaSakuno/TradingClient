using TradingClient.Domain.Options;

namespace TradingClient.Application.UseCases.Options;

/// <summary>
/// T 型报价一行（一个行权价的 Call/Put 双侧）。
/// CallIv/PutIv 是对理论价的 IV 往返反解（引擎自证：应 ≈ 输入的 InputVol = σ(m)）；
/// null 表示反解失败（错误码见 ImpliedVolatility 注释），UI 对应单元格显示 "—"。
/// </summary>
public sealed record OptionQuoteRow(
    double Strike,
    double LogMoneyness,
    double InputVol,
    double CallTheo,
    double PutTheo,
    OptionGreeks CallGreeks,
    OptionGreeks PutGreeks,
    double? CallIv,
    double? PutIv);
