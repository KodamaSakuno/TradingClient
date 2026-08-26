namespace TradingClient.Exchanges.Gate.Models;

internal sealed record GateWsRequest(
    long Time,
    string Channel,
    string Event,
    IReadOnlyList<string> Payload);

internal sealed record GateWsPingRequest(long Time, string Channel);
