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
            });

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
        }
        finally
        {
            File.Delete(path);
        }
    }
}
