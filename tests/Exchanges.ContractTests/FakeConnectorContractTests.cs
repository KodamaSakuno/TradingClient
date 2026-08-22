using TradingClient.Application.Abstractions;
using TradingClient.Domain.Primitives;
using TradingClient.Exchanges.ContractTests.Contract;
using TradingClient.Exchanges.ContractTests.Fakes;

namespace TradingClient.Exchanges.ContractTests;

/// <summary>按账户模式（Classic / Unified）参数化的 fixture</summary>
public class FakeClassicSpotTradingTests : SpotTradingContractTests
{
    protected override ISpotTrading CreateConnector() => new FakeExchangeConnector(AccountMode.Classic);
}

public class FakeUnifiedSpotTradingTests : SpotTradingContractTests
{
    protected override ISpotTrading CreateConnector() => new FakeExchangeConnector(AccountMode.Unified);
}

public class FakeClassicFuturesTradingTests : FuturesTradingContractTests
{
    protected override IFuturesTrading CreateConnector() => new FakeExchangeConnector(AccountMode.Classic);
}

public class FakeUnifiedFuturesTradingTests : FuturesTradingContractTests
{
    protected override IFuturesTrading CreateConnector() => new FakeExchangeConnector(AccountMode.Unified);
}

public class FakeClassicAccountServiceTests : AccountServiceContractTests
{
    protected override IAccountService CreateConnector() => new FakeExchangeConnector(AccountMode.Classic);
}

public class FakeUnifiedAccountServiceTests : AccountServiceContractTests
{
    protected override IAccountService CreateConnector() => new FakeExchangeConnector(AccountMode.Unified);
}

public class FakeClassicMarketDataTests : MarketDataContractTests
{
    protected override IMarketData CreateConnector() => new FakeExchangeConnector(AccountMode.Classic);
}

public class FakeUnifiedMarketDataTests : MarketDataContractTests
{
    protected override IMarketData CreateConnector() => new FakeExchangeConnector(AccountMode.Unified);
}
