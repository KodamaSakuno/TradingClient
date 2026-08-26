using TradingClient.Domain.Primitives;

namespace TradingClient.Domain.Instruments;

public sealed record Instrument(
    Symbol Symbol,
    decimal TickSize,
    decimal StepSize,
    decimal MinQuantity,
    decimal? MinQuoteAmount,
    decimal? ContractMultiplier,
    InstrumentStatus Status)
{
    public ProductKind Product => Symbol.Product;

    public decimal AlignPrice(decimal price) => decimal.Floor(price / TickSize) * TickSize;

    public decimal AlignQuantity(decimal quantity) => decimal.Floor(quantity / StepSize) * StepSize;

    public Result ValidateOrder(decimal? price, decimal quantity)
    {
        if (Status != InstrumentStatus.Trading)
            return Result.Failure(new ExchangeError(
                "INSTRUMENT_NOT_TRADING", $"{Symbol.Raw} is not tradable."));
        if (price is not null && (price <= 0 || price % TickSize != 0))
            return Result.Failure(new ExchangeError(
                "INVALID_PRICE", $"Price must be a positive multiple of tick size {TickSize}."));
        if (quantity < MinQuantity)
            return Result.Failure(new ExchangeError(
                "QUANTITY_TOO_SMALL", $"Quantity must be at least {MinQuantity}."));
        if (quantity % StepSize != 0)
            return Result.Failure(new ExchangeError(
                "INVALID_QUANTITY", $"Quantity must be a multiple of step size {StepSize}."));
        // 市价单无价格无法计算名义金额，跳过；Gate 等交易所对限价单强制 min_quote_amount
        if (MinQuoteAmount is not null && price is not null && price.Value * quantity < MinQuoteAmount)
            return Result.Failure(new ExchangeError(
                "NOTIONAL_TOO_SMALL", $"Notional value must be at least {MinQuoteAmount}."));
        return Result.Success();
    }
}
