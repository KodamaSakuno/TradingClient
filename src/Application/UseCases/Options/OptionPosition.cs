using TradingClient.Domain.Instruments;

namespace TradingClient.Application.UseCases.Options;

/// <summary>
/// 期权持仓（mock）。Quantity 单位为标的数量（吨），与商品期权报价单位（元/吨）一致：
/// 正 = 买入持有（long），负 = 卖出（short）。OpenPrice 为开仓权利金（元/吨），仅供展示。
/// </summary>
public sealed record OptionPosition(OptionSymbol Symbol, double Quantity, double OpenPrice);
