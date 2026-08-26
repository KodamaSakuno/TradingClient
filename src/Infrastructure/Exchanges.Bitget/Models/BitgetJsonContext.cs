using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Bitget.Models;

[JsonSerializable(typeof(BitgetResponse<BitgetInstrument[]>))]
[JsonSerializable(typeof(BitgetResponse<BitgetServerTime>))]
[JsonSerializable(typeof(BitgetResponse<BitgetAccountAssets>))]
[JsonSerializable(typeof(BitgetResponse<BitgetOrderAck>))]
[JsonSerializable(typeof(BitgetApiError))]
[JsonSerializable(typeof(BitgetPlaceOrderRequest))]
[JsonSerializable(typeof(BitgetCancelOrderRequest))]
[JsonSerializable(typeof(BitgetWsRequest))]
[JsonSerializable(typeof(BitgetWsLoginRequest))]
[JsonSerializable(typeof(BitgetWsEnvelope))]
[JsonSerializable(typeof(BitgetWsTicker[]))]
[JsonSerializable(typeof(BitgetWsPublicTrade[]))]
[JsonSerializable(typeof(BitgetWsBook[]))]
[JsonSerializable(typeof(BitgetWsOrder[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class BitgetJsonContext : JsonSerializerContext;
