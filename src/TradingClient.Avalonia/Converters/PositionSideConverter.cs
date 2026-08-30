using System.Globalization;
using Avalonia.Data.Converters;
using TradingClient.Domain.Trading;

namespace TradingClient.Avalonia.Converters;

/// <summary>持仓方向中文化显示</summary>
public sealed class PositionSideConverter : IValueConverter
{
    public static readonly PositionSideConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PositionSide.Long => "多",
            PositionSide.Short => "空",
            PositionSide.Both => "双向",
            _ => value?.ToString(),
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
