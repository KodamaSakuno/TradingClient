using System.Text.Json;

namespace TradingClient.Exchanges.Gate.Models;

internal sealed record GateWsEnvelope(
    long Time,
    long? TimeMs,
    string? Channel,
    string? Event,
    GateWsError? Error,
    JsonElement Result);

internal sealed record GateWsError(int Code, string Message);
