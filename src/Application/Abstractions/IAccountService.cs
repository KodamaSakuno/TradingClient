using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;

namespace TradingClient.Application.Abstractions;

public interface IAccountService : IExchangeConnector
{
    Task<Result<AccountSummary>> GetAccountAsync(CancellationToken ct);

    Task<Result> TransferFundsAsync(TransferRequest req, CancellationToken ct);
}

public sealed record TransferRequest(
    string Asset,
    decimal Amount,
    string FromAccount,
    string ToAccount);
