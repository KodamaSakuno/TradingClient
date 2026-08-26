using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Common;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;

namespace TradingClient.Infrastructure.Tests;

public class GateConnectorSpotTradingTests
{
    // 形态对齐 .local/gate_api_spot_restful.txt 的录制订单对象（FULL 模式）
    private const string OpenOrderJson = """
        {
          "id": "62167231234",
          "text": "apiv4",
          "create_time": "1710488334",
          "update_time": "1710488334",
          "create_time_ms": 1710488334073,
          "update_time_ms": 1710488334074,
          "status": "open",
          "currency_pair": "BTC_USDT",
          "type": "limit",
          "account": "spot",
          "side": "buy",
          "amount": "0.01",
          "price": "50000",
          "time_in_force": "gtc",
          "left": "0.01",
          "filled_total": "0",
          "finish_as": "open"
        }
        """;

    private const string FilledOrderJson = """
        {
          "id": "62167231235",
          "create_time_ms": 1710488334073,
          "update_time_ms": 1710488334074,
          "status": "closed",
          "currency_pair": "BTC_USDT",
          "type": "limit",
          "account": "spot",
          "side": "buy",
          "amount": "0.01",
          "price": "50000",
          "time_in_force": "gtc",
          "left": "0",
          "filled_total": "500",
          "finish_as": "filled"
        }
        """;

    private static readonly SpotSymbol BtcUsdt = new("BTC", "USDT");

    [Fact]
    public async Task PlaceSpotOrderAsync_WithLimitBuy_SendsExpectedRequestAndMapsOrder()
    {
        var connector = CreateConnector(_ => OkJson(OpenOrderJson), out var captured);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var order = result.Value!;
        Assert.Equal("62167231234", order.OrderId);
        Assert.Equal(BtcUsdt, order.Symbol);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(OrderType.Limit, order.Type);
        Assert.Equal(50_000m, order.Price);
        Assert.Equal(0.01m, order.Quantity);
        Assert.Equal(0m, order.FilledQuantity);
        Assert.Equal(OrderStatus.New, order.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1710488334073), order.CreatedAt);

        var (request, body) = Assert.Single(captured.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v4/spot/orders", request.RequestUri!.AbsolutePath);

        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("BTC_USDT", root.GetProperty("currency_pair").GetString());
        Assert.Equal("limit", root.GetProperty("type").GetString());
        Assert.Equal("buy", root.GetProperty("side").GetString());
        Assert.Equal("0.01", root.GetProperty("amount").GetString());
        Assert.Equal("50000", root.GetProperty("price").GetString());
        Assert.Equal("gtc", root.GetProperty("time_in_force").GetString());
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithMarketSell_SendsIocAndBaseAmountWithoutPrice()
    {
        const string marketOrderJson = """
            {
              "id": "62167231236",
              "create_time_ms": 1710488334073,
              "update_time_ms": 1710488334074,
              "status": "closed",
              "currency_pair": "BTC_USDT",
              "type": "market",
              "account": "spot",
              "side": "sell",
              "amount": "0.5",
              "time_in_force": "ioc",
              "left": "0",
              "filled_total": "32450.5",
              "finish_as": "filled"
            }
            """;
        var connector = CreateConnector(_ => OkJson(marketOrderJson), out var captured);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Sell, OrderType.Market, null, 0.5m),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var order = result.Value!;
        Assert.Equal(OrderType.Market, order.Type);
        Assert.Null(order.Price);
        Assert.Equal(0.5m, order.Quantity);
        Assert.Equal(0.5m, order.FilledQuantity);
        Assert.Equal(OrderStatus.Filled, order.Status);

        var (_, body) = Assert.Single(captured.Requests);
        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("market", root.GetProperty("type").GetString());
        // Gate 语义坑：market sell 的 amount 是 base 币数量
        Assert.Equal("0.5", root.GetProperty("amount").GetString());
        Assert.Equal("ioc", root.GetProperty("time_in_force").GetString());
        Assert.False(root.TryGetProperty("price", out _));
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithMarketBuy_ReturnsUnsupportedOrderWithoutHttpCall()
    {
        var connector = CreateConnector(_ => OkJson(OpenOrderJson), out var captured);

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
        var connector = CreateConnector(_ => OkJson(OpenOrderJson), out var captured);

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
        var connector = CreateConnector(_ => OkJson(OpenOrderJson), out var captured);

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
        var connector = new GateConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("[]"))),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException());

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("MISSING_CREDENTIALS", result.Error!.Code);
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithBalanceNotEnough_ReturnsLabelCodedFailure()
    {
        const string errorJson = """{"label":"BALANCE_NOT_ENOUGH","message":"Not enough balance"}""";
        var connector = CreateConnector(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errorJson, Encoding.UTF8, "application/json"),
        }, out _);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("BALANCE_NOT_ENOUGH", result.Error!.Code);
        Assert.Equal("Not enough balance", result.Error.Message);
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WithClosedOrder_MapsFilledStatus()
    {
        var connector = CreateConnector(_ => OkJson(FilledOrderJson), out _);

        var result = await connector.PlaceSpotOrderAsync(
            new PlaceSpotOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 50_000m, 0.01m),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Filled, result.Value!.Status);
        Assert.Equal(0.01m, result.Value.FilledQuantity);
    }

    [Fact]
    public async Task PlaceSpotOrderAsync_WhenRateLimited_DelaysSecondCall()
    {
        // capacity=1, refill=1/s：第二单必须等约 1 秒补令牌，用时间下限确定性验证限流器已接线
        var connector = CreateConnector(_ => OkJson(OpenOrderJson), out _,
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
    public async Task CancelSpotOrderAsync_SendsDeleteWithCurrencyPairQuery()
    {
        var connector = CreateConnector(_ => OkJson(OpenOrderJson), out var captured);

        var result = await connector.CancelSpotOrderAsync(
            BtcUsdt, "62167231234", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var (request, _) = Assert.Single(captured.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/api/v4/spot/orders/62167231234", request.RequestUri!.AbsolutePath);
        Assert.Equal("currency_pair=BTC_USDT", request.RequestUri.Query.TrimStart('?'));
    }

    [Fact]
    public async Task CancelSpotOrderAsync_WithUnknownOrder_ReturnsLabelCodedFailure()
    {
        const string errorJson = """{"label":"ORDER_NOT_FOUND","message":"Order not found"}""";
        var connector = CreateConnector(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(errorJson, Encoding.UTF8, "application/json"),
        }, out _);

        var result = await connector.CancelSpotOrderAsync(
            BtcUsdt, "no-such-order", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("ORDER_NOT_FOUND", result.Error!.Code);
    }

    [Fact]
    public async Task CancelSpotOrderAsync_WithoutCredentials_ReturnsMissingCredentialsFailure()
    {
        var connector = new GateConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("[]"))),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException());

        var result = await connector.CancelSpotOrderAsync(
            BtcUsdt, "62167231234", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("MISSING_CREDENTIALS", result.Error!.Code);
    }

    [Fact]
    public async Task CancelSpotOrderAsync_WhenRateLimited_DelaysSecondCall()
    {
        // 撤单与下单共用同一个 10r/s 令牌桶（Gate 对下单+改单+撤单合并限频）
        var connector = CreateConnector(_ => OkJson(OpenOrderJson), out _,
            new TokenBucketRateLimiter(capacity: 1, refillPerSecond: 1));

        var first = await connector.CancelSpotOrderAsync(
            BtcUsdt, "62167231234", TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess);

        var elapsed = Stopwatch.StartNew();
        var second = await connector.CancelSpotOrderAsync(
            BtcUsdt, "62167231234", TestContext.Current.CancellationToken);
        elapsed.Stop();

        Assert.True(second.IsSuccess);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(500),
            $"Second call should wait for a token, took {elapsed.Elapsed}.");
    }

    private static GateConnector CreateConnector(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        out CapturingHandler captured,
        TokenBucketRateLimiter? spotRateLimiter = null)
    {
        var handler = new CapturingHandler(responder);
        captured = handler;
        return new GateConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson("[]"))),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException(),
            credentials: new GateCredentials("test-key", "test-secret"),
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
