using System.Collections.Concurrent;

namespace TradingClient.Application.Risk.Rules;

/// <summary>
/// 按 symbol 的当日下单量上限：只有网关成功下单才计入（经 IRiskOrderPlacedHook），
/// 跨天自动清零；时钟注入便于测试。
/// </summary>
public sealed class DailyVolumeLimitRule(RiskLimitsProfile profile, TimeProvider clock)
    : IPreTradeRiskCheck, IRiskOrderPlacedHook
{
    private readonly ConcurrentDictionary<string, DailyVolume> _volumes = new();

    public string RuleName => "DailyVolumeLimit";

    public ValueTask<RiskRejection?> CheckAsync(RiskCheckContext context, CancellationToken ct)
    {
        var limit = profile.For(context.Symbol.Raw).MaxDailyQuantity;
        var used = UsedToday(context.Symbol.Raw);
        return ValueTask.FromResult(used + context.Quantity > limit
            ? new RiskRejection(RuleName, "DAILY_VOLUME_EXCEEDED",
                $"Daily volume {used} + {context.Quantity} would exceed limit {limit} for {context.Symbol.Raw}.")
            : (RiskRejection?)null);
    }

    public void OnOrderPlaced(RiskCheckContext context)
    {
        var today = Today();
        _volumes.AddOrUpdate(
            context.Symbol.Raw,
            new DailyVolume(today, context.Quantity),
            (_, current) => current.Day == today
                ? current with { Quantity = current.Quantity + context.Quantity }
                : new DailyVolume(today, context.Quantity));
    }

    private decimal UsedToday(string symbolRaw) =>
        _volumes.TryGetValue(symbolRaw, out var volume) && volume.Day == Today()
            ? volume.Quantity
            : 0m;

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private sealed record DailyVolume(DateOnly Day, decimal Quantity);
}
