namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// spot.trades
/// </summary>
internal sealed record GateTrade(
    long Id,
    string Side,
    string CurrencyPair,
    string Amount,
    string Price,
    long? CreateTime,
    string? CreateTimeMs);
