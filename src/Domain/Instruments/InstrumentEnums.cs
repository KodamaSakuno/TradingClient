namespace TradingClient.Domain.Instruments;

public enum ProductKind
{
    Spot,
    Futures,
    Options,
}

public enum ContractKind
{
    Perpetual,
    Delivery,
}

public enum OptionRight
{
    Call,
    Put,
}