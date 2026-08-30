using System.Globalization;
using Avalonia.Data.Converters;
using TradingClient.Domain.Trading;

namespace TradingClient.Avalonia.Converters;

/// <summary>保证金模式中文化显示</summary>
public sealed class MarginModeConverter : IValueConverter
{
    public static readonly MarginModeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            MarginMode.Cross => "全仓",
            MarginMode.Isolated => "逐仓",
            MarginMode.PortfolioMargin => "组合",
            _ => value?.ToString(),
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
