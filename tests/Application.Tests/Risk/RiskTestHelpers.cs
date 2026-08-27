using TradingClient.Application.Abstractions;
using TradingClient.Application.Risk;
using TradingClient.Application.Tests.Fakes;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Tests.Risk;

internal static class RiskTestHelpers
{
    public static readonly SpotSymbol BtcUsdt = new("BTC", "USDT");

    public static readonly RiskRuleConfig DefaultConfig = new(
        MaxOrderQuantity: 10m,
        MaxDailyQuantity: 100m,
        MaxPositionQuantity: 50m,
        MaxPriceDeviationRatio: 0.05m,
        DuplicatePriceToleranceRatio: 0.001,
        DuplicateWindow: TimeSpan.FromSeconds(3));

    public static RiskLimitsProfile Profile(RiskRuleConfig? config = null) =>
        new(config ?? DefaultConfig, new Dictionary<string, RiskRuleConfig>());

    public static RiskCheckContext Context(
        IExchangeConnector? connector = null,
        Symbol? symbol = null,
        OrderSide side = OrderSide.Buy,
        OrderType type = OrderType.Limit,
        decimal? price = 100m,
        decimal quantity = 1m,
        decimal? latestPrice = null,
        decimal? position = null) =>
        new(connector ?? new FakeSpotTrading(),
            symbol ?? BtcUsdt, side, type, price, quantity, latestPrice, position);
}
