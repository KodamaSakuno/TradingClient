using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

[JsonSerializable(typeof(GateCurrencyPair[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class GateJsonContext : JsonSerializerContext;
