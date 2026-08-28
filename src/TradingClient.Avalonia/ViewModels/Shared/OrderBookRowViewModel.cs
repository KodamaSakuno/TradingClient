using Avalonia.Media;
using ReactiveUI;

namespace TradingClient.Avalonia.ViewModels.Shared;

/// <summary>
/// 订单簿单行展示模型：价格、数量、累计深度、深度条宽度。
/// </summary>
public sealed class OrderBookRowViewModel : ReactiveObject
{
    private decimal _price;
    public decimal Price
    {
        get => _price;
        set => this.RaiseAndSetIfChanged(ref _price, value);
    }

    private decimal _quantity;
    public decimal Quantity
    {
        get => _quantity;
        set => this.RaiseAndSetIfChanged(ref _quantity, value);
    }

    private decimal _cumulativeQuantity;
    public decimal CumulativeQuantity
    {
        get => _cumulativeQuantity;
        set => this.RaiseAndSetIfChanged(ref _cumulativeQuantity, value);
    }

    private double _depthPercent;
    public double DepthPercent
    {
        get => _depthPercent;
        set => this.RaiseAndSetIfChanged(ref _depthPercent, value);
    }

    public IBrush PriceBrush { get; init; } = Brushes.White;

    public int PriceDecimals { get; init; } = 2;
    public int QuantityDecimals { get; init; } = 4;

    public string PriceText => Price.ToString($"F{PriceDecimals}");
    public string QuantityText => Quantity.ToString($"F{QuantityDecimals}");
    public string CumulativeText => CumulativeQuantity.ToString($"F{QuantityDecimals}");
}
