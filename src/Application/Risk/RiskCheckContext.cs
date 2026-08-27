using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Risk;

/// <summary>
/// 风控上下文：由调用方（下单用例）组装。
/// LatestPrice / CurrentPositionQuantity 取自 IRiskSnapshotSource；快照源查不到该 Symbol
/// （如现货 Symbol 不在 RiskMonitor 的监控表内）时为 null，依赖它们的规则跳过。
/// </summary>
public sealed record RiskCheckContext(
    IExchangeConnector Connector,
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    decimal? Price,
    decimal Quantity,
    decimal? LatestPrice,
    decimal? CurrentPositionQuantity);
