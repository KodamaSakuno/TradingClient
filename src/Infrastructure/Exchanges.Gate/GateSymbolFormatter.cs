using System.Globalization;
using TradingClient.Domain.Instruments;

namespace TradingClient.Exchanges.Gate;

/// <summary>
/// Gate 原生符号字符串与领域 Symbol 的双向转换
/// 现货与永续合约同为 BASE_QUOTE
/// 交割合约为 BASE_QUOTE_yyyyMMdd
/// 符号一律大写
/// </summary>
public static class GateSymbolFormatter
{
    private const string DateFormat = "yyyyMMdd";

    public static string FormatSpot(SpotSymbol symbol) =>
        $"{symbol.Base.ToUpperInvariant()}_{symbol.Quote.ToUpperInvariant()}";

    public static string FormatFutures(FuturesSymbol symbol) => symbol switch
    {
        PerpetualFuturesSymbol perp => $"{perp.Base.ToUpperInvariant()}_{perp.Quote.ToUpperInvariant()}",
        DeliveryFuturesSymbol delivery =>
            $"{delivery.Base.ToUpperInvariant()}_{delivery.Quote.ToUpperInvariant()}" +
            $"_{delivery.Expiry.ToString(DateFormat, CultureInfo.InvariantCulture)}",
        _ => throw new ArgumentException($"Unsupported futures symbol type: {symbol.GetType().Name}", nameof(symbol)),
    };

    public static SpotSymbol ParseSpot(string raw)
    {
        var (baseAsset, quoteAsset) = SplitIntoBaseAndQuote(raw);
        return new SpotSymbol(baseAsset, quoteAsset);
    }

    public static FuturesSymbol ParseFutures(string raw)
    {
        var normalized = Normalize(raw);
        var count = SplitSegments(normalized, out var baseAsset, out var quoteAsset, out var expirySegment);

        return count switch
        {
            2 => new PerpetualFuturesSymbol(baseAsset.ToString(), quoteAsset.ToString()),
            3 => DateOnly.TryParseExact(expirySegment, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
                ? new DeliveryFuturesSymbol(baseAsset.ToString(), quoteAsset.ToString(), expiry)
                : throw new ArgumentException($"Invalid delivery date '{expirySegment.ToString()}' in Gate futures symbol '{normalized}'.", nameof(raw)),
            _ => throw new ArgumentException($"Gate futures symbol '{normalized}' must have 2 or 3 segments.", nameof(raw)),
        };
    }

    private static (string Base, string Quote) SplitIntoBaseAndQuote(string raw)
    {
        var normalized = Normalize(raw);
        var count = SplitSegments(normalized, out var baseAsset, out var quoteAsset, out _);

        return count == 2
            ? (baseAsset.ToString(), quoteAsset.ToString())
            : throw new ArgumentException($"Gate symbol '{normalized}' must have exactly 2 segments.", nameof(raw));
    }

    private static ReadOnlySpan<char> Normalize(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        return raw.Trim().ToUpperInvariant();
    }

    private static int SplitSegments(ReadOnlySpan<char> source,
        out ReadOnlySpan<char> first, out ReadOnlySpan<char> second, out ReadOnlySpan<char> third)
    {
        var span = source;

        first = default;
        second = default;
        third = default;

        var count = 0;

        while (true)
        {
            var separator = span.IndexOf('_');
            var segment = separator < 0 ? span : span[..separator];
            if (segment.IsEmpty)
            {
                throw new ArgumentException($"Gate symbol '{source}' contains an empty segment.", nameof(source));
            }

            switch (count)
            {
                case 0: first = segment; break;
                case 1: second = segment; break;
                case 2: third = segment; break;
                default: return count + 1;
            }

            count++;

            if (separator < 0)
            {
                return count;
            }

            span = span[(separator + 1)..];
        }
    }
}
