namespace TradingClient.Domain.Instruments;

/// <summary>
/// 语义化交易符号，禁止用裸字符串表示交易对
/// 与交易所原生字符串的双向转换由各适配器的 SymbolFormatter 负责
/// </summary>
public abstract record Symbol(string Raw)
{
    public abstract ProductKind Product { get; }
}

public sealed record SpotSymbol(string Base, string Quote)
    : Symbol($"{Base}/{Quote}")
{
    public override ProductKind Product => ProductKind.Spot;
}

public abstract record FuturesSymbol(string Base, string Quote, string Raw)
    : Symbol(Raw)
{
    public override ProductKind Product => ProductKind.Futures;

    public abstract ContractKind Kind { get; }
}

public sealed record PerpetualFuturesSymbol(string Base, string Quote)
    : FuturesSymbol(Base, Quote, $"{Base}/{Quote}:PERP")
{
    public override ContractKind Kind => ContractKind.Perpetual;
}

public sealed record DeliveryFuturesSymbol(string Base, string Quote, DateOnly Expiry)
    : FuturesSymbol(Base, Quote, $"{Base}/{Quote}:{Expiry:yyyy-MM-dd}")
{
    public override ContractKind Kind => ContractKind.Delivery;
}

public sealed record OptionSymbol(string Underlying, DateOnly Expiry, decimal Strike, OptionRight Right)
    : Symbol($"{Underlying}-{Expiry:yyyy-MM-dd}-{Strike}-{Right}")
{
    public override ProductKind Product => ProductKind.Options;
}
