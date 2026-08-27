using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Risk;

/// <summary>
/// 风控上下文：由调用方（下单用例）组装。
/// LatestPrice / CurrentPositionQuantity 目前无快照源，由调用方传 null，
/// 依赖它们的规则跳过（下单票面板 UI 接入后才有真实来源）。
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
