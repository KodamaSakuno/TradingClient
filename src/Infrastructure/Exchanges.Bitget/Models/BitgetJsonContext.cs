using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Bitget.Models;

[JsonSerializable(typeof(BitgetResponse<BitgetInstrument[]>))]
[JsonSerializable(typeof(BitgetResponse<BitgetServerTime>))]
[JsonSerializable(typeof(BitgetResponse<BitgetAccountAssets>))]
[JsonSerializable(typeof(BitgetApiError))]
[JsonSerializable(typeof(BitgetWsRequest))]
[JsonSerializable(typeof(BitgetWsEnvelope))]
[JsonSerializable(typeof(BitgetWsTicker[]))]
[JsonSerializable(typeof(BitgetWsPublicTrade[]))]
[JsonSerializable(typeof(BitgetWsBook[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BitgetJsonContext : JsonSerializerContext;
