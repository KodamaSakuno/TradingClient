using TradingClient.Domain.Instruments;

namespace TradingClient.Exchanges.Bitget;

/// <summary>
/// Bitget 原生符号字符串与领域 Symbol 的双向转换
/// 现货符号为无分隔符大写拼接：BTCUSDT
/// 合约符号（V3 category 区分产品线）留待阶段 3 实现
/// </summary>
public static class BitgetSymbolFormatter
{
    // 现货符号无分隔符，反向解析依赖已知计价币后缀表、最长匹配优先；
    // 后缀表可能漏掉新上线的计价币，属已知限制。
    // instruments 接口的映射走返回体中的 baseCoin/quoteCoin 字段，不经此解析。
    // 表必须保持按长度降序排列，匹配逻辑依赖该顺序。
    private static readonly string[] QuoteSuffixes =
    [
        "USDT", "USDC", "FDUSD", "TUSD", "DAI", "USD",
        "BTC", "ETH", "BGB", "EUR", "GBP", "TRY", "BRL", "JPY", "AUD", "RUB", "UAH",
    ];

    public static string FormatSpot(SpotSymbol symbol) =>
        $"{symbol.Base.ToUpperInvariant()}{symbol.Quote.ToUpperInvariant()}";

    public static SpotSymbol ParseSpot(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        var normalized = raw.Trim().ToUpperInvariant();

        foreach (var suffix in QuoteSuffixes)
        {
            if (!normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var baseAsset = normalized[..^suffix.Length];
            if (baseAsset.Length == 0)
            {
                break;
            }

            return new SpotSymbol(baseAsset, suffix);
        }

        throw new ArgumentException(
            $"Bitget symbol '{normalized}' does not end with a known quote asset.", nameof(raw));
    }
}
