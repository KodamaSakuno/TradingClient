using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Trading;

/// <summary>
/// 本地盘口维护器：把 OrderBookDelta 流（快照 + 增量）维护成内存中的完整盘口。
/// 纯数据结构与算法，零依赖，是 BenchmarkDotNet 基线目标路径（基准项目阶段 6 再建），
/// 热路径（Apply）不用 LINQ，保持分配敏感。
/// delta 语义（适配器约定）：IsSnapshot 全量替换；档位 Quantity==0 表示删除该价位。
/// 不做序列号校验：领域模型无 seq 字段，Gate U/u、Bitget pseq 不外传是适配器层的既定决策，
/// upsert 语义对乱序/重复 delta 天然幂等（同价位重复应用结果相同）。
/// </summary>
public sealed class LocalOrderBook
{
    // bids 降序（最优价在首位），asks 升序；两个字典的首元素即 BestBid/BestAsk
    private readonly SortedDictionary<decimal, decimal> _bids = new(Comparer<decimal>.Create((x, y) => y.CompareTo(x)));
    private readonly SortedDictionary<decimal, decimal> _asks = new();

    public Symbol? Symbol { get; private set; }

    public void Apply(OrderBookDelta delta)
    {
        if (delta.IsSnapshot)
        {
            _bids.Clear();
            _asks.Clear();
        }

        Symbol = delta.Symbol;
        ApplySide(_bids, delta.Bids);
        ApplySide(_asks, delta.Asks);
    }

    private static void ApplySide(SortedDictionary<decimal, decimal> side, IReadOnlyList<OrderBookLevel> levels)
    {
        // 热路径：手写索引循环，不用 LINQ
        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            if (level.Quantity == 0m)
                side.Remove(level.Price);
            else
                side[level.Price] = level.Quantity;
        }
    }

    /// <summary>买盘最优档（价最高）；空盘口为 null</summary>
    public OrderBookLevel? BestBid => FirstLevel(_bids);

    /// <summary>卖盘最优档（价最低）；空盘口为 null</summary>
    public OrderBookLevel? BestAsk => FirstLevel(_asks);

    private static OrderBookLevel? FirstLevel(SortedDictionary<decimal, decimal> side)
    {
        // SortedDictionary 按序枚举，首元素即最优档
        using var e = side.GetEnumerator();
        return e.MoveNext() ? new OrderBookLevel(e.Current.Key, e.Current.Value) : null;
    }

    /// <summary>
    /// 取某侧前 depth 档，Bids 降序 / Asks 升序（最优档在前）。
    /// 每次调用分配新列表，调用方按节流后的频率使用（150ms 一次），不在逐 tick 路径上。
    /// </summary>
    public List<OrderBookLevel> GetTop(OrderSide side, int depth)
    {
        var book = side == OrderSide.Buy ? _bids : _asks;
        var result = new List<OrderBookLevel>(Math.Min(depth, book.Count));
        foreach (var (price, quantity) in book)
        {
            if (result.Count >= depth)
                break;
            result.Add(new OrderBookLevel(price, quantity));
        }
        return result;
    }
}
