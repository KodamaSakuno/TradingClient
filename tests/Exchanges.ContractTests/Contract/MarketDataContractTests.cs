using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;

namespace TradingClient.Exchanges.ContractTests.Contract;

/// <summary>IMarketData 契约测试基类。每个连接器实现按账户模式（Classic / Unified）各派生一个 fixture。</summary>
public abstract class MarketDataContractTests
{
    protected abstract IMarketData CreateConnector();

    public static TheoryData<ProductKind> Products =>
    [
        ProductKind.Spot,
        ProductKind.Futures
    ];

    [Theory]
    [MemberData(nameof(Products))]
    public async Task GetInstrumentsAsync_ReturnsInstrumentsOfRequestedProduct(ProductKind product)
    {
        var connector = CreateConnector();

        var instruments = await connector.GetInstrumentsAsync(product, TestContext.Current.CancellationToken);

        Assert.NotEmpty(instruments);
        Assert.All(instruments, i => Assert.Equal(product, i.Product));
    }
}
