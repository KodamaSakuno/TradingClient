using TradingClient.Domain.Instruments;

namespace TradingClient.Domain.Primitives;

public enum AccountMode
{
    Classic,
    Unified,
}

public sealed record ExchangeCapabilities(
    AccountMode AccountMode,
    bool RequiresInternalTransfers,
    IReadOnlyList<ProductKind> Products);
