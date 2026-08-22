using TradingClient.Application.Abstractions;
using TradingClient.Domain.Primitives;

namespace TradingClient.Exchanges.ContractTests.Contract;

/// <summary>
/// IAccountService 契约测试基类。AccountSummary.Mode 必须与 Capabilities 声明一致。
/// 每个连接器实现按账户模式（Classic / Unified）各派生一个 fixture。
/// </summary>
public abstract class AccountServiceContractTests
{
    protected abstract IAccountService CreateConnector();

    [Fact]
    public async Task GetAccountAsync_SummaryModeMatchesDeclaredCapabilities()
    {
        var connector = CreateConnector();

        var result = await connector.GetAccountAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(connector.Capabilities.AccountMode, result.Value!.Mode);
    }

    [Fact]
    public void Capabilities_DeclareInternalTransfersOnlyForClassicMode()
    {
        var connector = CreateConnector();

        Assert.Equal(
            connector.Capabilities.AccountMode == AccountMode.Classic,
            connector.Capabilities.RequiresInternalTransfers);
    }
}
