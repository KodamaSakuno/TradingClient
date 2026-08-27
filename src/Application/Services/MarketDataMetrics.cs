using System.Diagnostics.Metrics;

namespace TradingClient.Application.Services;

/// <summary>
/// 行情链路性能埋点（§9.2）：delta 计数、单笔 Apply 耗时、端到端延迟。
/// System.Diagnostics.Metrics 为运行时内置，无新增包；静态持有 Meter，进程级单例。
/// 端到端延迟口径：交易所 delta.Timestamp 与本地时钟存在未校正的偏移（时间偏移维护在适配器 Auth 层，
/// 不外传），该值含时钟偏移，仅供趋势观察，不作为精确延迟。
/// </summary>
public static class MarketDataMetrics
{
    public const string MeterName = "TradingClient.MarketData";

    private static readonly Meter s_meter = new(MeterName);

    private static readonly Counter<long> s_deltasReceived =
        s_meter.CreateCounter<long>("marketdata.deltas.received", description: "收到的订单簿 delta 数");

    private static readonly Counter<long> s_deltasApplied =
        s_meter.CreateCounter<long>("marketdata.deltas.applied", description: "应用到本地盘口的 delta 数");

    private static readonly Histogram<double> s_applyDuration =
        s_meter.CreateHistogram<double>("marketdata.apply.duration", unit: "us", description: "单笔 delta 应用到本地盘口耗时");

    private static readonly Histogram<double> s_endToEndLatency =
        s_meter.CreateHistogram<double>("marketdata.e2e.latency", unit: "ms", description: "delta.Timestamp 到 Apply 完成的端到端延迟（含时钟偏移）");

    public static void RecordDeltaReceived() => s_deltasReceived.Add(1);

    public static void RecordDeltaApplied(double applyDurationUs, double endToEndLatencyMs)
    {
        s_deltasApplied.Add(1);
        s_applyDuration.Record(applyDurationUs);
        s_endToEndLatency.Record(endToEndLatencyMs);
    }
}
