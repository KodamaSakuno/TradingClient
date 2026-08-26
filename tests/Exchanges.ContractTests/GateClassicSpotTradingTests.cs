using System.Net;
using System.Text;
using TradingClient.Application.Abstractions;
using TradingClient.Exchanges.ContractTests.Contract;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;
using TradingClient.Exchanges.Gate.WebSocket;

namespace TradingClient.Exchanges.ContractTests;

public class GateClassicSpotTradingTests : SpotTradingContractTests
{
    private static readonly string OrderJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate_spot_order_placed.json"));

    // spot.orders 私有频道通知（result 为订单数组），id 与 gate_spot_order_placed.json 一致，
    // 供基类用例断言"下单后收到该单的推送"；形态对齐 .local/gate_api_spot_ws.txt 的通知示例
    private static readonly string OrderUpdateJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate_spot_order_update.json"));

    protected override ISpotTrading CreateConnector() =>
        new GateConnector(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => new ReplayingWsTransport(OrderUpdateJson),
            credentials: new GateCredentials("test-key", "test-secret"),
            authInnerHandler: new StubHttpMessageHandler(request =>
                request.Method == HttpMethod.Delete
                    // 未知订单撤单：回放 Gate 拒单错误体
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(
                            """{"label":"ORDER_NOT_FOUND","message":"Order not found"}""",
                            Encoding.UTF8, "application/json"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(OrderJson, Encoding.UTF8, "application/json"),
                    }));

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    // 收到 spot.orders 订阅帧后回放一条订单通知，模拟下单后的私有推送
    private sealed class ReplayingWsTransport(string orderNotification) : IGateWsTransport
    {
        private readonly Lock _gate = new();
        private readonly Queue<string?> _inbound = new();
        private readonly SemaphoreSlim _signal = new(0);

        public Task ConnectAsync(Uri endpoint, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(string message, CancellationToken ct)
        {
            if (message.Contains("\"channel\":\"spot.orders\"") && message.Contains("\"event\":\"subscribe\""))
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
