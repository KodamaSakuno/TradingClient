using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Abstractions;

public interface IMarketData : IExchangeConnector
{
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(ProductKind product, CancellationToken ct);

    IObservable<Quote> SubscribeQuotes(Symbol symbol);

    IObservable<Trade> SubscribeTrades(Symbol symbol);

    IObservable<OrderBookDelta> SubscribeOrderBook(Symbol symbol);

    IObservable<Candle> SubscribeCandles(Symbol symbol, TimeFrame tf);
}
