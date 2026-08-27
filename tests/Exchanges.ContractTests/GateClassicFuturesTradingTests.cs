using System.Net;
using System.Text;
using TradingClient.Application.Abstractions;
using TradingClient.Exchanges.Common;
using TradingClient.Exchanges.ContractTests.Contract;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;

namespace TradingClient.Exchanges.ContractTests;

public class GateClassicFuturesTradingTests : FuturesTradingContractTests
{
    // 张→币乘数缓存走公共 contracts 拉取；BTC_USDT 的 quanto_multiplier=0.0001（基类 0.01 BTC = 100 张）
    private static readonly string ContractsJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate_futures_usdt_contracts.json"));

    // POST /futures/usdt/orders 响应（2026-08-27 取自 .local/gate_api_futures_p_restful.md 下单响应示例，裁剪为映射所需字段）
    private static readonly string OrderPlacedJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate_futures_usdt_order_placed.json"));

    // GET /futures/usdt/positions 响应数组（出处同上 positions 列表 schema）
    private static readonly string PositionsJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate_futures_usdt_positions.json"));

    // futures.positions 私有通知（2026-08-27 取自 .local/gate_api_futures_p_ws.md 通知示例）；
    // mode="single" 且 size 为负 → 映射出 Short，供基类 PositionUpdates_CarryPositionSide 断言
    private static readonly string PositionUpdateJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gate_futures_usdt_position_update.json"));

    protected override IFuturesTrading CreateConnector() =>
        new GateConnector(
            new HttpClient(new StubHttpMessageHandler(RoutePublic)),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException(), // 本 fixture 不用现货 WS
            credentials: new GateCredentials("test-key", "test-secret"),
            authInnerHandler: new StubHttpMessageHandler(RouteAuthenticated),
            futuresWsTransportFactory: () => new ReplayingWsTransport(PositionUpdateJson));

    private static HttpResponseMessage RoutePublic(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/futures/usdt/contracts", StringComparison.Ordinal)
            ? OkJson(ContractsJson)
            : OkJson("{}");

    private static HttpResponseMessage RouteAuthenticated(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path.EndsWith("/futures/usdt/orders", StringComparison.Ordinal))
            return OkJson(OrderPlacedJson);
        // DELETE /futures/usdt/orders：撤销全部 open 订单（kill switch，§6.4），成功响应体无映射需求
        if (request.Method == HttpMethod.Delete && path.EndsWith("/futures/usdt/orders", StringComparison.Ordinal))
            return OkJson("{}");
        if (path.EndsWith("/futures/usdt/positions", StringComparison.Ordinal))
            return OkJson(PositionsJson);
        return OkJson("{}");
    }

    private static HttpResponseMessage OkJson(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    // 收到 futures.positions 订阅帧后回放一条持仓通知，模拟下单后的私有推送（现货 fixture 同款模式）
    private sealed class ReplayingWsTransport(string positionNotification) : IWsTransport
    {
        private readonly Lock _gate = new();
        private readonly Queue<string?> _inbound = new();
        private readonly SemaphoreSlim _signal = new(0);

        public Task ConnectAsync(Uri endpoint, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(string message, CancellationToken ct)
        {
            if (message.Contains("\"channel\":\"futures.positions\"") && message.Contains("\"event\":\"subscribe\""))
                Push(positionNotification);
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
