using System.Collections.Concurrent;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;

namespace TradingClient.Application.Services;

/// <summary>
/// 单个连接器实例的 instruments 进程内缓存：按 ProductKind 惰性全量加载，之后按 Symbol 查询。
/// 不写磁盘、无 TTL；规则变更由 RefreshAsync 强制刷新。
/// </summary>
public sealed class InstrumentCache(IMarketData marketData)
{
    private readonly ConcurrentDictionary<Symbol, Instrument> _instruments = new();
    // 每条产品线一把锁：并发首次加载只触发一次拉取
    private readonly ConcurrentDictionary<ProductKind, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<ProductKind, byte> _loaded = new();

    public async Task<Instrument?> GetAsync(Symbol symbol, CancellationToken ct)
    {
        await EnsureLoadedAsync(symbol.Product, ct);
        return _instruments.GetValueOrDefault(symbol);
    }

    public async Task RefreshAsync(ProductKind product, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(product, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var instruments = await marketData.GetInstrumentsAsync(product, ct);
            // 先清掉该产品线的旧条目，避免已下架的 instrument 残留
            foreach (var symbol in _instruments.Keys.Where(s => s.Product == product))
                _instruments.TryRemove(symbol, out _);
            foreach (var instrument in instruments)
                _instruments[instrument.Symbol] = instrument;
            _loaded[product] = 1;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(ProductKind product, CancellationToken ct)
    {
        if (_loaded.ContainsKey(product))
            return;
        var gate = _gates.GetOrAdd(product, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_loaded.ContainsKey(product))
                return;
            // 拉取抛异常时不标记 loaded，下次调用自然重试
            var instruments = await marketData.GetInstrumentsAsync(product, ct);
            foreach (var instrument in instruments)
                _instruments[instrument.Symbol] = instrument;
            _loaded[product] = 1;
        }
        finally
        {
            gate.Release();
        }
    }
}
