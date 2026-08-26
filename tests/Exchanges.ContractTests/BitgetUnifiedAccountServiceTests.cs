using System.Net;
using System.Text;
using TradingClient.Application.Abstractions;
using TradingClient.Exchanges.Bitget;
using TradingClient.Exchanges.Bitget.Auth;
using TradingClient.Exchanges.ContractTests.Contract;

namespace TradingClient.Exchanges.ContractTests;

public class BitgetUnifiedAccountServiceTests : AccountServiceContractTests
{
    // fixture 出处：官方文档示例响应（.local/bitget/catalog/uta-account-assets-balance/assets-balance-assets.md），2026-08-26
    private static readonly string AccountAssetsJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bitget_v3_account_assets.json"));

    protected override IAccountService CreateConnector() =>
        new BitgetConnector(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            BitgetConnector.DefaultBaseUrl,
            // 账户契约测试不触发 WS 订阅，传输工厂不会被调用
            new Uri("wss://localhost/ws"),
            () => throw new InvalidOperationException("WS transport is not used in this test."),
            credentials: new BitgetCredentials("test-key", "test-secret", "test-passphrase"),
            demoTrading: false,
            authInnerHandler: new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(AccountAssetsJson, Encoding.UTF8, "application/json"),
            }));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
