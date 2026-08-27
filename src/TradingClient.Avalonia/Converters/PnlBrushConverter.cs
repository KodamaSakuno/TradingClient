using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TradingClient.Avalonia.Converters;

/// <summary>未实现盈亏着色：正绿负红，非数值输入回退灰色</summary>
public sealed class PnlBrushConverter : IValueConverter
{
    public static readonly PnlBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            decimal pnl when pnl > 0 => Brushes.ForestGreen,
            decimal pnl when pnl < 0 => Brushes.Red,
            decimal => Brushes.Gray,
            _ => Brushes.Gray,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
