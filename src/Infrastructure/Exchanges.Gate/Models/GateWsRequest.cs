using System.Text.Json.Serialization;

namespace TradingClient.Exchanges.Gate.Models;

internal sealed record GateWsRequest(
    long Time,
    string Channel,
    string Event,
    IReadOnlyList<string> Payload);

// 私有频道的 subscribe/unsubscribe 请求体直接携带 auth，无独立登录帧
internal sealed record GateWsAuthenticatedRequest(
    long Time,
    string Channel,
    string Event,
    IReadOnlyList<string> Payload,
    GateWsAuth Auth);

// KEY/SIGN 必须大写，不能被全局 SnakeCaseLower 命名策略改写
internal sealed record GateWsAuth(
    string Method,
    [property: JsonPropertyName("KEY")] string Key,
    [property: JsonPropertyName("SIGN")] string Sign);

internal sealed record GateWsPingRequest(long Time, string Channel);
