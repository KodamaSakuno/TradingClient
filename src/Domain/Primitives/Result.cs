namespace TradingClient.Domain.Primitives;

public class Result
{
    public bool IsSuccess { get; }
    public ExchangeError? Error { get; }

    protected Result(bool isSuccess, ExchangeError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, null);
    public static Result Failure(ExchangeError error) => new(false, error);

    public static Result<T> Success<T>(T value) => new(value);
    public static Result<T> Failure<T>(ExchangeError error) => new(error);
}

public sealed class Result<T> : Result
{
    public T? Value { get; }

    internal Result(T value) : base(true, null)
    {
        Value = value;
    }

    internal Result(ExchangeError error) : base(false, error)
    {
        Value = default;
    }
}

public sealed record ExchangeError(string Code, string Message);
