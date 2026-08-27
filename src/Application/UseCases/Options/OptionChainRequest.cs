namespace TradingClient.Application.UseCases.Options;

/// <summary>
/// 期权链计算输入。StrikeCount 档围绕平值档（Forward 对齐到 StrikeStep 的整数倍）对称展开，
/// 国内商品期权行权价为整档（豆粕 50 元/吨）。
/// </summary>
public sealed record OptionChainRequest(
    double Forward,
    double Rate,
    SmileParameters Smile,
    DateOnly ValuationDate,
    DateOnly Expiry,
    int StrikeCount,
    double StrikeStep);
