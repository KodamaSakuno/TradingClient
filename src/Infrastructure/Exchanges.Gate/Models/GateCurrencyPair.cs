namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// Gate /spot/currency_pairs
/// </summary>
internal sealed record GateCurrencyPair(
    string Id,
    string Base,
    string Quote,
    int Precision,
    int AmountPrecision,
    string? MinBaseAmount,
    string TradeStatus);
