namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// spot.tickers
/// </summary>
internal sealed record GateTicker(
    string CurrencyPair,
    string? Last,
    string? LowestAsk,
    string? HighestBid);
