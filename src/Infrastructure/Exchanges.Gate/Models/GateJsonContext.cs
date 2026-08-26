using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

[JsonSerializable(typeof(GateCurrencyPair[]))]
[JsonSerializable(typeof(GateSpotAccount[]))]
[JsonSerializable(typeof(GateApiError))]
[JsonSerializable(typeof(GateServerTime))]
[JsonSerializable(typeof(GateWsRequest))]
[JsonSerializable(typeof(GateWsPingRequest))]
[JsonSerializable(typeof(GateWsEnvelope))]
[JsonSerializable(typeof(GateTicker))]
[JsonSerializable(typeof(GateTrade))]
[JsonSerializable(typeof(GateOrderBookUpdate))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class GateJsonContext : JsonSerializerContext;
