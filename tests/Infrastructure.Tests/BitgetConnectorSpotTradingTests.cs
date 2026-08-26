using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Bitget;
using TradingClient.Exchanges.Bitget.Auth;
using TradingClient.Exchanges.Common;

namespace TradingClient.Infrastructure.Tests;

public class BitgetConnectorSpotTradingTests
{
    // 响应形态对齐官方文档示例（.local/bitget/catalog/trading-order-management/uta-trade-order.md）：
    // V3 下单响应仅含 orderId/clientOid，不含订单状态
    private const string PlaceOrderAckJson = """
        {
          "code": "00000",
          "msg": "success",
          "requestTime": 1695806875837,
          "data": { "orderId": "121211212122", "clientOid": "test-client-oid" }
        }
        """;

    private static readonly SpotSymbol BtcUsdt = new("BTC", "USDT");

    [Fact]
    public async Task PlaceSpotOrderAsync_WithLimitBuy_SendsExpectedRequestAndMapsOrder()
    {
        var connector = CreateConnector(_ => OkJson(PlaceOrderAckJson), out var captured);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var order = result.Value!;
        Assert.Equal("121211212122", order.OrderId);
        Assert.Equal(BtcUsdt, order.Symbol);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(OrderType.Limit, order.Type);
        Assert.Equal(50_000m, order.Price);
        Assert.Equal(0.01m, order.Quantity);
        // V3 下单响应不含成交信息，映射固定为 New + 0 成交
        Assert.Equal(0m, order.FilledQuantity);
        Assert.Equal(OrderStatus.New, order.Status);

        var (request, body) = Assert.Single(captured.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v3/trade/place-order", request.RequestUri!.AbsolutePath);

        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("SPOT", root.GetProperty("category").GetString());
        Assert.Equal("BTCUSDT", root.GetProperty("symbol").GetString());
        Assert.Equal("buy", root.GetProperty("side").GetString());
        Assert.Equal("limit", root.GetProperty("orderType").GetString());
        Assert.Equal("0.01", root.GetProperty("qty").GetString());
        Assert.Equal("50000", root.GetProperty("price").GetString());
        Assert.Equal("gtc", root.GetProperty("timeInForce").GetString());
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithMarketSell_SendsBaseQtyWithoutPrice()
    {
        var connector = CreateConnector(_ => OkJson(PlaceOrderAckJson), out var captured);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Sell, OrderType.Market, null, 0.5m),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderType.Market, result.Value!.Type);
        Assert.Null(result.Value.Price);

        var (_, body) = Assert.Single(captured.Requests);
        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("market", root.GetProperty("orderType").GetString());
        // Bitget 语义坑：市价卖单的 qty 是 base 币数量（市价买单才是 quote 金额）
        Assert.Equal("0.5", root.GetProperty("qty").GetString());
        Assert.False(root.TryGetProperty("price", out _));
        Assert.False(root.TryGetProperty("timeInForce", out _));
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithMarketBuy_ReturnsUnsupportedOrderWithoutHttpCall()
    {
        var connector = CreateConnector(_ => OkJson(PlaceOrderAckJson), out var captured);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Market, null, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("UNSUPPORTED_ORDER", result.Error!.Code);
        Assert.Empty(captured.Requests);
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithZeroQuantity_ReturnsInvalidQuantityWithoutHttpCall()
    {
        var connector = CreateConnector(_ => OkJson(PlaceOrderAckJson), out var captured);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0m),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_QUANTITY", result.Error!.Code);
        Assert.Empty(captured.Requests);
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_LimitWithoutPrice_ReturnsMissingPriceWithoutHttpCall()
    {
        var connector = CreateConnector(_ => OkJson(PlaceOrderAckJson), out var captured);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, null, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("MISSING_PRICE", result.Error!.Code);
        Assert.Empty(captured.Requests);
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithoutCredentials_ReturnsMissingCredentialsFailure()
    {
        var connector = new BitgetConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("[]"))),
            BitgetConnector.DefaultBaseUrl,
            new Uri(BitgetConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException(),
            credentials: null,
            demoTrading: false);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("MISSING_CREDENTIALS", result.Error!.Code);
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithHttp200BusinessError_ReturnsEnvelopeCodedFailure()
    {
        // Bitget 怪癖：HTTP 200 下返回业务错误信封
        const string errorJson = """{"code":"40010","msg":"Request timed out","requestTime":1695806875837,"data":null}""";
        var connector = CreateConnector(_ => OkJson(errorJson), out _);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("40010", result.Error!.Code);
        Assert.Equal("Request timed out", result.Error.Message);
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithHttpError_ReturnsBodyCodedFailure()
    {
        const string errorJson = """{"code":"43012","msg":"Insufficient balance"}""";
        var connector = CreateConnector(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errorJson, Encoding.UTF8, "application/json"),
        }, out _);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("43012", result.Error!.Code);
        Assert.Equal("Insufficient balance", result.Error.Message);
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WhenRateLimited_DelaysSecondCall()
    {
        // capacity=1, refill=1/s：第二单必须等约 1 秒补令牌，用时间下限确定性验证限流器已接线
        var connector = CreateConnector(_ => OkJson(PlaceOrderAckJson), out _,
            new TokenBucketRateLimiter(capacity: 1, refillPerSecond: 1));

        var first = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess);

        var elapsed = Stopwatch.StartNew();
        var second = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);
        elapsed.Stop();

        Assert.True(second.IsSuccess);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(500),
            $"Second call should wait for a token, took {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task CancelSpotOrderAsync_SendsExpectedRequest()
    {
        var connector = CreateConnector(_ => OkJson(PlaceOrderAckJson), out var captured);

        var result = await connector.CancelSpotOrderAsync(
            BtcUsdt, "121211212122", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var (request, body) = Assert.Single(captured.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v3/trade/cancel-order", request.RequestUri!.AbsolutePath);

        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("121211212122", root.GetProperty("orderId").GetString());
        Assert.Equal("SPOT", root.GetProperty("category").GetString());
    }

    [Fact]
    public async Task CancelSpotOrderAsync_WithHttp200BusinessError_ReturnsEnvelopeCodedFailure()
    {
        const string errorJson = """{"code":"22001","msg":"Order does not exist","requestTime":1695806875837,"data":null}""";
        var connector = CreateConnector(_ => OkJson(errorJson), out _);

        var result = await connector.CancelSpotOrderAsync(
            BtcUsdt, "no-such-order", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("22001", result.Error!.Code);
    }

    [Fact]
    public async Task CancelSpotOrderAsync_WithoutCredentials_ReturnsMissingCredentialsFailure()
    {
        var connector = new BitgetConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("[]"))),
            BitgetConnector.DefaultBaseUrl,
            new Uri(BitgetConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException(),
            credentials: null,
            demoTrading: false);

        var result = await connector.CancelSpotOrderAsync(
            BtcUsdt, "121211212122", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("MISSING_CREDENTIALS", result.Error!.Code);
    }

    [Fact]
    public async Task CancelSpotOrderAsync_WhenRateLimited_SharesBucketWithPlace()
    {
        // 下单与撤单共用一个 10r/s 令牌桶
        var connector = CreateConnector(_ => OkJson(PlaceOrderAckJson), out _,
            new TokenBucketRateLimiter(capacity: 1, refillPerSecond: 1));

        var first = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess);

        var elapsed = Stopwatch.StartNew();
        var second = await connector.CancelSpotOrderAsync(
            BtcUsdt, "121211212122", TestContext.Current.CancellationToken);
        elapsed.Stop();

        Assert.True(second.IsSuccess);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(500),
            $"Cancel after place should wait for a token, took {elapsed.Elapsed}.");
    }

    private static BitgetConnector CreateConnector(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        out CapturingHandler captured,
        TokenBucketRateLimiter? spotRateLimiter = null)
    {
        var handler = new CapturingHandler(responder);
        captured = handler;
        return new BitgetConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("[]"))),
            BitgetConnector.DefaultBaseUrl,
            new Uri(BitgetConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException(),
            credentials: new BitgetCredentials("test-key", "test-secret", "test-passphrase"),
            demoTrading: false,
            authInnerHandler: handler,
            spotRateLimiter: spotRateLimiter);
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

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return responder(request);
        }
    }
}
