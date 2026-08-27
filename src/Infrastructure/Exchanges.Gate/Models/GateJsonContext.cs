using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

[JsonSerializable(typeof(GateCurrencyPair[]))]
[JsonSerializable(typeof(GateFuturesContract[]))]
[JsonSerializable(typeof(GateSpotAccount[]))]
[JsonSerializable(typeof(GateSpotOrderRequest))]
[JsonSerializable(typeof(GateSpotOrder))]
[JsonSerializable(typeof(GateApiError))]
[JsonSerializable(typeof(GateServerTime))]
[JsonSerializable(typeof(GateWsRequest))]
[JsonSerializable(typeof(GateWsAuthenticatedRequest))]
[JsonSerializable(typeof(GateWsPingRequest))]
[JsonSerializable(typeof(GateWsEnvelope))]
[JsonSerializable(typeof(GateTicker))]
[JsonSerializable(typeof(GateTrade))]
[JsonSerializable(typeof(GateOrderBookUpdate))]
[JsonSerializable(typeof(GateSpotOrderUpdate[]))]
[JsonSerializable(typeof(GateFuturesBookTicker))]
[JsonSerializable(typeof(GateFuturesTrade[]))]
[JsonSerializable(typeof(GateFuturesOrderBookUpdate))]
[JsonSerializable(typeof(GateFuturesOrderRequest))]
[JsonSerializable(typeof(GateFuturesOrder))]
[JsonSerializable(typeof(GateFuturesPosition[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class GateJsonContext : JsonSerializerContext;
