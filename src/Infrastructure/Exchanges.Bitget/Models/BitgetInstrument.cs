namespace TradingClient.Exchanges.Bitget.Models;

/// <summary>
/// Bitget V3 /market/instruments 的 data 项。
/// 与 Gate 相同，Bitget 数值字段（含精度）一律以字符串返回。
/// </summary>
internal sealed record BitgetInstrument(
    string Symbol,
    string Category,
    string BaseCoin,
    string QuoteCoin,
    string MinOrderQty,
    string PricePrecision,
    string QuantityPrecision,
    string? MinOrderAmount,
    string Status);
