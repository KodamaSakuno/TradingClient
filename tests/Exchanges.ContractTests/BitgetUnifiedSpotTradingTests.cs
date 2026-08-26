using System.Net;
using System.Text;
using TradingClient.Application.Abstractions;
using TradingClient.Exchanges.Bitget;
using TradingClient.Exchanges.Bitget.Auth;
using TradingClient.Exchanges.Common;
using TradingClient.Exchanges.ContractTests.Contract;

namespace TradingClient.Exchanges.ContractTests;

public class BitgetUnifiedSpotTradingTests : SpotTradingContractTests
{
    // fixture 出处：官方文档示例响应（.local/bitget/catalog/trading-order-management/uta-trade-order.md），2026-08-26；
    // V3 下单响应仅含 orderId/clientOid
    private static readonly string OrderPlacedJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bitget_v3_spot_order_placed.json"));

    // 私有 order 频道通知，orderId 与 bitget_v3_spot_order_placed.json 一致，
    // 供基类用例断言"下单后收到该单的推送"；形态对齐 .local/bitget/uta/websocket/private/Order-Channel.md 示例
    private static readonly string OrderUpdateJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bitget_v3_order_update.json"));

    protected override ISpotTrading CreateConnector() =>
        new BitgetConnector(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            BitgetConnector.DefaultBaseUrl,
            new Uri("wss://localhost/ws/public"),
            wsTransportFactory: () => new ReplayingWsTransport(OrderUpdateJson),
            credentials: new BitgetCredentials("test-key", "test-secret", "test-passphrase"),
            demoTrading: false,
            authInnerHandler: new StubHttpMessageHandler(request =>
                request.RequestUri!.AbsolutePath.EndsWith("cancel-order")
                    // 未知订单撤单：回放 HTTP 200 下的业务错误信封（Bitget 怪癖，code != "00000" 即失败）
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """{"code":"22001","msg":"Order does not exist","requestTime":1695806875837,"data":null}""",
                            Encoding.UTF8, "application/json"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(OrderPlacedJson, Encoding.UTF8, "application/json"),
                    }));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    // 私有端点握手回放：收到 login 帧回 login 成功；收到 order 订阅帧后回放一条订单通知，模拟下单后的私有推送
    private sealed class ReplayingWsTransport(string orderNotification) : IWsTransport
    {
        private const string LoginSuccessJson = """{"event":"login","code":"0","msg":""}""";

        private readonly Lock _gate = new();
        private readonly Queue<string?> _inbound = new();
        private readonly SemaphoreSlim _signal = new(0);

        public Task ConnectAsync(Uri endpoint, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(string message, CancellationToken ct)
        {
            if (message.Contains("\"op\":\"login\""))
                Push(LoginSuccessJson);
            else if (message.Contains("\"op\":\"subscribe\"") && message.Contains("\"topic\":\"order\""))
                Push(orderNotification);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct);
            lock (_gate)
                return _inbound.Dequeue();
        }

        private void Push(string? message)
        {
            lock (_gate)
                _inbound.Enqueue(message);
            _signal.Release();
        }

        public void Abort() { }

        public void Dispose() => _signal.Dispose();
    }
}
