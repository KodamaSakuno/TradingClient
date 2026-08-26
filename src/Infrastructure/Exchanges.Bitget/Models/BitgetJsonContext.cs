using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Bitget.Models;

[JsonSerializable(typeof(BitgetResponse<BitgetInstrument[]>))]
[JsonSerializable(typeof(BitgetResponse<BitgetServerTime>))]
[JsonSerializable(typeof(BitgetResponse<BitgetAccountAssets>))]
[JsonSerializable(typeof(BitgetApiError))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BitgetJsonContext : JsonSerializerContext;
