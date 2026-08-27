using TradingClient.Application.Risk;
using TradingClient.Persistence;

namespace TradingClient.Infrastructure.Tests;

public class JsonRiskLimitsStoreTests
{
    private static string TempFilePath() =>
        Path.Combine(Path.GetTempPath(), $"risk-limits-{Guid.NewGuid():N}.json");

    private static RiskLimitsProfile SampleProfile() =>
        new(
            new RiskRuleConfig(
                MaxOrderQuantity: 1m,
                MaxDailyQuantity: 10m,
                MaxPositionQuantity: 5m,
                MaxPriceDeviationRatio: 0.05m,
                DuplicatePriceToleranceRatio: 0.001,
                DuplicateWindow: TimeSpan.FromSeconds(3)),
            new Dictionary<string, RiskRuleConfig>
            {
                ["BTC_USDT"] = new RiskRuleConfig(
                    MaxOrderQuantity: 0.5m,
                    MaxDailyQuantity: 2m,
                    MaxPositionQuantity: 1m,
                    MaxPriceDeviationRatio: 0.02m,
                    DuplicatePriceToleranceRatio: 0.0005,
                    DuplicateWindow: TimeSpan.FromSeconds(10)),
            },
            Monitor: new RiskMonitorConfig(
                DailyLossWarning: 50m,
                DailyLossReduceOnly: 120m,
                DailyLossLocked: 250m,
                ExposureWarning: 5_000m,
                ExposureReduceOnly: 8_000m,
                KillSwitchOnLocked: true,
                KillSwitchOnDisconnect: false,
                DayCutOffset: TimeSpan.FromHours(8)));

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsNull()
    {
        var store = new JsonRiskLimitsStore(TempFilePath());

        var profile = await store.LoadAsync(CancellationToken.None);

        Assert.Null(profile);
    }

    [Fact]
    public async Task SaveThenLoad_RoundtripsProfile()
    {
        var path = TempFilePath();
        try
        {
            var store = new JsonRiskLimitsStore(path);
            var original = SampleProfile();

            await store.SaveAsync(original, CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(original.Default, loaded.Default);
            Assert.Equal(original.PerSymbol, loaded.PerSymbol);
            Assert.Equal(original.Monitor, loaded.Monitor);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_LegacyFileWithoutMonitor_FallsBackToDefaultMonitorConfig()
    {
        // 旧版 risk-limits.json 无 monitor 字段：Monitor 为 null，MonitorOrDefault 回落内置默认
        var path = TempFilePath();
        try
        {
            await File.WriteAllTextAsync(path, """
                {
                  "default": {
                    "maxOrderQuantity": 1,
                    "maxDailyQuantity": 10,
                    "maxPositionQuantity": 5,
                    "maxPriceDeviationRatio": 0.05,
                    "duplicatePriceToleranceRatio": 0.001,
                    "duplicateWindow": "00:00:03"
                  },
                  "perSymbol": {}
                }
                """, TestContext.Current.CancellationToken);

            var loaded = await new JsonRiskLimitsStore(path).LoadAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Null(loaded.Monitor);
            Assert.Equal(RiskMonitorConfig.Default, loaded.MonitorOrDefault);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
