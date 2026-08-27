using TradingClient.Domain.Instruments;

namespace TradingClient.Application.Risk;

/// <summary>
/// 风控快照源：向事前风控链提供两张最新快照——最新价与带符号净持仓。
/// 由 RiskMonitor 实现（它内部本就维护这两张表），下单用例组装 RiskCheckContext 时查询。
/// 数据只对监控覆盖的 Symbol 可见：查不到返回 null，依赖它们的规则按既有约定跳过。
/// </summary>
public interface IRiskSnapshotSource
{
    /// <summary>最优买卖中价；该 Symbol 无行情订阅或无数据时返回 null。</summary>
    decimal? GetLatestPrice(Symbol symbol);

    /// <summary>带符号净持仓（多正空负，dual 模式两腿合并）；无持仓/监控不覆盖（如现货）返回 null。</summary>
    decimal? GetCurrentPositionQuantity(Symbol symbol);
}
