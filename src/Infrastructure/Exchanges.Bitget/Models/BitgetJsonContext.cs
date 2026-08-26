using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Bitget.Models;

[JsonSerializable(typeof(BitgetResponse<BitgetInstrument[]>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BitgetJsonContext : JsonSerializerContext;
