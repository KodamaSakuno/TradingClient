namespace TradingClient.Domain.Instruments;

public sealed record Instrument(Symbol Symbol)
{
    public ProductKind Product => Symbol.Product;
}
