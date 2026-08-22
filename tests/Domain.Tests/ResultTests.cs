using TradingClient.Domain.Primitives;

namespace TradingClient.Domain.Tests;

public class ResultTests
{
    [Fact]
    public void Success_CarriesNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_CarriesErrorCodeAndMessage()
    {
        var error = new ExchangeError("INSUFFICIENT_BALANCE", "Not enough USDT.");

        var result = Result.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.Equal("INSUFFICIENT_BALANCE", result.Error?.Code);
        Assert.Equal("Not enough USDT.", result.Error?.Message);
    }

    [Fact]
    public void SuccessOfT_ExposesValue()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void FailureOfT_HasDefaultValue()
    {
        var result = Result.Failure<string>(new ExchangeError("X", "failed"));

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal("X", result.Error?.Code);
    }
}
