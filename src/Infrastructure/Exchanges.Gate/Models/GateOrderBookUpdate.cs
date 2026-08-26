using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

/// <summary>
/// spot.order_book_update
/// </summary>
internal sealed record GateOrderBookUpdate(
    [property: JsonPropertyName("t")] long UpdateTimeMs,
    [property: JsonPropertyName("full")] bool? Full,
    [property: JsonPropertyName("s")] string CurrencyPair,
    [property: JsonPropertyName("U")] long FirstUpdateId,
    [property: JsonPropertyName("u")] long LastUpdateId,
    [property: JsonPropertyName("b")] string[][]? Bids,
    [property: JsonPropertyName("a")] string[][]? Asks);
