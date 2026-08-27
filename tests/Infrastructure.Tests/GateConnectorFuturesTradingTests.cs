using System.Net;
using System.Text;
using System.Text.Json;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;

namespace TradingClient.Infrastructure.Tests;

public class GateConnectorFuturesTradingTests
{
    // 乘数缓存走公共 contracts 拉取（GateConnector.EnsureFuturesContractsCachedAsync），桩公共客户端返回本 fixture
    private const string ContractsJson = """
        [
          {
            "name": "BTC_USDT",
            "quanto_multiplier": "0.0001",
            "order_price_round": "0.1",
            "order_size_min": 1,
            "order_size_max": 1000000,
            "enable_decimal": false,
            "status": "trading",
            "in_delisting": false
          }
        ]
        """;

    // 形态对齐 .local/gate_api_futures_p_restful.md：id 裸数字、size/left 字符串（双态混排同录制样本）
    private const string OpenOrderJson = """
        {
          "id": 987654321,
          "contract": "BTC_USDT",
          "create_time": 1761200000.5,
          "size": "100",
          "left": "100",
          "price": "79000",
          "fill_price": "0",
          "tif": "gtc",
          "status": "open"
        }
        """;

    private const string FilledOrderJson = """
        {
          "id": 987654322,
          "contract": "BTC_USDT",
          "create_time": 1761200001,
          "size": "100",
          "left": "0",
          "price": "79000",
          "fill_price": "79000",
          "tif": "gtc",
          "status": "finished",
          "finish_as": "filled"
        }
        """;

    private const string PositionsJson = """
        [
          {
            "contract": "BTC_USDT",
            "size": "200",
            "entry_price": "79000.5",
            "liq_price": "75000",
            "mark_price": "79100",
            "unrealised_pnl": "20.01",
            "leverage": "0",
            "cross_leverage_limit": "25",
            "mode": "single"
          },
          {
            "contract": "BTC_USDT",
            "size": "-50",
            "entry_price": "80500",
            "liq_price": "85000",
            "mark_price": "80000",
            "unrealised_pnl": "-1.25",
            "leverage": "10",
            "cross_leverage_limit": "0",
            "mode": "single"
          }
        ]
        """;

    private static readonly PerpetualFuturesSymbol BtcUsdt = new("BTC", "USDT");

    [Fact]
    public async Task PlaceFuturesOrderAsync_WithLimitBuy_SendsExpectedRequestAndMapsOrder()
    {
        var connector = CreateConnector(_ => CreatedJson(OpenOrderJson), out var captured);

        var result = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 79_000m, 0.01m,
                PositionSide.Long, MarginMode.Cross, Leverage: null),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var order = result.Value!;
        Assert.Equal("987654321", order.OrderId);
        Assert.Equal(BtcUsdt, order.Symbol);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(OrderType.Limit, order.Type);
        Assert.Equal(79_000m, order.Price);
        // 100 张 × 0.0001 = 0.01 币（§7 数量语义）
        Assert.Equal(0.01m, order.Quantity);
        Assert.Equal(0m, order.FilledQuantity);
        Assert.Equal(OrderStatus.New, order.Status);
        Assert.Equal(PositionSide.Long, order.PositionSide);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1761200000500), order.CreatedAt);

        var (request, body) = Assert.Single(captured.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v4/futures/usdt/orders", request.RequestUri!.AbsolutePath);

        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("BTC_USDT", root.GetProperty("contract").GetString());
        Assert.Equal(100, root.GetProperty("size").GetInt64());
        Assert.Equal("79000", root.GetProperty("price").GetString());
        Assert.Equal("gtc", root.GetProperty("tif").GetString());
    }

    [Fact]
    public async Task PlaceFuturesOrderAsync_WithFilledResponse_MapsFilledStatusAndQuantity()
    {
        var connector = CreateConnector(_ => CreatedJson(FilledOrderJson), out _);

        var result = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 79_000m, 0.01m,
                PositionSide.Long, MarginMode.Cross, Leverage: null),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Filled, result.Value!.Status);
        Assert.Equal(0.01m, result.Value.FilledQuantity);
    }

    [Fact]
    public async Task PlaceFuturesOrderAsync_WithMarketSell_SendsZeroPriceIocAndNegativeSize()
    {
        const string marketOrderJson = """
            {
              "id": 987654323,
              "contract": "BTC_USDT",
              "create_time": 1761200002,
              "size": "-100",
              "left": "0",
              "price": "0",
              "fill_price": "78950",
              "tif": "ioc",
              "status": "finished",
              "finish_as": "filled"
            }
            """;
        var connector = CreateConnector(_ => CreatedJson(marketOrderJson), out var captured);

        var result = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(BtcUsdt, OrderSide.Sell, OrderType.Market, null, 0.01m,
                PositionSide.Short, MarginMode.Cross, Leverage: null),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var order = result.Value!;
        Assert.Equal(OrderType.Market, order.Type);
        Assert.Null(order.Price);
        Assert.Equal(OrderSide.Sell, order.Side);
        Assert.Equal(PositionSide.Short, order.PositionSide);
        Assert.Equal(0.01m, order.FilledQuantity);

        var (_, body) = Assert.Single(captured.Requests);
        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        // 市价单协议形态：price "0" + tif ioc；卖出 size 为负
        Assert.Equal(-100, root.GetProperty("size").GetInt64());
        Assert.Equal("0", root.GetProperty("price").GetString());
        Assert.Equal("ioc", root.GetProperty("tif").GetString());
    }

    [Fact]
    public async Task PlaceFuturesOrderAsync_WithNonWholeContracts_ReturnsInvalidQuantityWithoutHttpCall()
    {
        var connector = CreateConnector(_ => CreatedJson(OpenOrderJson), out var captured);

        // 0.01505 币 / 0.0001 = 150.5 张，不整除（enable_decimal=false 张数为整数）
        var result = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 79_000m, 0.01505m,
                PositionSide.Long, MarginMode.Cross, Leverage: null),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_QUANTITY", result.Error!.Code);
        Assert.Empty(captured.Requests);
    }

    [Fact]
    public async Task PlaceFuturesOrderAsync_WithZeroQuantity_ReturnsInvalidQuantityWithoutHttpCall()
    {
        var connector = CreateConnector(_ => CreatedJson(OpenOrderJson), out var captured);

        var result = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 79_000m, 0m,
                PositionSide.Long, MarginMode.Cross, Leverage: null),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_QUANTITY", result.Error!.Code);
        Assert.Empty(captured.Requests);
    }

    [Fact]
    public async Task PlaceFuturesOrderAsync_WithoutCredentials_ReturnsMissingCredentialsFailure()
    {
        var connector = new GateConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson(ContractsJson))),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException());

        var result = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 79_000m, 0.01m,
                PositionSide.Long, MarginMode.Cross, Leverage: null),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("MISSING_CREDENTIALS", result.Error!.Code);
    }

    [Fact]
    public async Task PlaceFuturesOrderAsync_WithLeverage_SetsLeverageBeforePlacingOrder()
    {
        var connector = CreateConnector(
            req => req.RequestUri!.AbsolutePath.Contains("set_leverage")
                ? OkJson("{}")
                : CreatedJson(OpenOrderJson),
            out var captured);

        var result = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 79_000m, 0.01m,
                PositionSide.Long, MarginMode.Cross, Leverage: 10),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, captured.Requests.Count);
        // Gate 杠杆挂在持仓维度：先 set_leverage 再下单
        var (leverageRequest, _) = captured.Requests[0];
        Assert.Equal(HttpMethod.Post, leverageRequest.Method);
        Assert.Equal("/api/v4/futures/usdt/positions/BTC_USDT/set_leverage", leverageRequest.RequestUri!.AbsolutePath);
        Assert.Equal("leverage=10&margin_mode=cross", leverageRequest.RequestUri.Query.TrimStart('?'));
        Assert.Equal("/api/v4/futures/usdt/orders", captured.Requests[1].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PlaceFuturesOrderAsync_WithRejection_ReturnsLabelCodedFailure()
    {
        const string errorJson = """{"label":"BALANCE_NOT_ENOUGH","message":"Not enough balance"}""";
        var connector = CreateConnector(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errorJson, Encoding.UTF8, "application/json"),
        }, out _);

        var result = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(BtcUsdt, OrderSide.Buy, OrderType.Limit, 79_000m, 0.01m,
                PositionSide.Long, MarginMode.Cross, Leverage: null),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("BALANCE_NOT_ENOUGH", result.Error!.Code);
        Assert.Equal("Not enough balance", result.Error.Message);
    }

    [Fact]
    public async Task PlaceFuturesOrderAsync_WithDeliverySymbol_ThrowsNotSupported()
    {
        var connector = CreateConnector(_ => CreatedJson(OpenOrderJson), out _);
        var delivery = new DeliveryFuturesSymbol("BTC", "USDT", new DateOnly(2026, 9, 25));

        await Assert.ThrowsAsync<NotSupportedException>(() => connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(delivery, OrderSide.Buy, OrderType.Limit, 79_000m, 0.01m,
                PositionSide.Long, MarginMode.Cross, Leverage: null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetLeverageAsync_SendsExpectedPathAndQuery()
    {
        var connector = CreateConnector(_ => OkJson("{}"), out var captured);

        var result = await connector.SetLeverageAsync(
            BtcUsdt, 20, MarginMode.Isolated, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var (request, _) = Assert.Single(captured.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/v4/futures/usdt/positions/BTC_USDT/set_leverage", request.RequestUri!.AbsolutePath);
        Assert.Equal("leverage=20&margin_mode=isolated", request.RequestUri.Query.TrimStart('?'));
    }

    [Fact]
    public async Task SetLeverageAsync_WithRejection_ReturnsLabelCodedFailure()
    {
        const string errorJson = """{"label":"LEVERAGE_INVALID","message":"Invalid leverage"}""";
        var connector = CreateConnector(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errorJson, Encoding.UTF8, "application/json"),
        }, out _);

        var result = await connector.SetLeverageAsync(
            BtcUsdt, 200, MarginMode.Cross, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal("LEVERAGE_INVALID", result.Error!.Code);
    }

    [Fact]
    public async Task SetLeverageAsync_WithPortfolioMargin_ThrowsArgumentException()
    {
        var connector = CreateConnector(_ => OkJson("{}"), out _);

        // PortfolioMargin 为 Domain 预留枚举值，Gate 无对应模式
        await Assert.ThrowsAsync<ArgumentException>(() => connector.SetLeverageAsync(
            BtcUsdt, 10, MarginMode.PortfolioMargin, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPositionsAsync_MapsSidesMarginModesAndCoinQuantity()
    {
        var connector = CreateConnector(_ => OkJson(PositionsJson), out var captured);

        var result = await connector.GetPositionsAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var (request, _) = Assert.Single(captured.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v4/futures/usdt/positions", request.RequestUri!.AbsolutePath);
        Assert.Equal("holding=true", request.RequestUri.Query.TrimStart('?'));

        var positions = result.Value!;
        Assert.Equal(2, positions.Count);

        // 全仓（leverage "0"）：实际杠杆取 cross_leverage_limit
        var longPosition = positions[0];
        Assert.Equal(BtcUsdt, longPosition.Symbol);
        Assert.Equal(PositionSide.Long, longPosition.Side);
        Assert.Equal(0.02m, longPosition.Quantity); // 200 张 × 0.0001
        Assert.Equal(79_000.5m, longPosition.EntryPrice);
        Assert.Equal(20.01m, longPosition.UnrealizedPnl);
        Assert.Equal(MarginMode.Cross, longPosition.MarginMode);
        Assert.Equal(25, longPosition.Leverage);

        // 逐仓（leverage 非 "0"）：杠杆即字段值；size 负 = 空头
        var shortPosition = positions[1];
        Assert.Equal(PositionSide.Short, shortPosition.Side);
        Assert.Equal(0.005m, shortPosition.Quantity); // 50 张 × 0.0001
        Assert.Equal(MarginMode.Isolated, shortPosition.MarginMode);
        Assert.Equal(10, shortPosition.Leverage);
        Assert.Equal(-1.25m, shortPosition.UnrealizedPnl);
    }

    private static GateConnector CreateConnector(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        out CapturingHandler captured)
    {
        var handler = new CapturingHandler(responder);
        captured = handler;
        return new GateConnector(
            new HttpClient(new StubHttpMessageHandler(_ => OkJson(ContractsJson))),
            GateConnector.DefaultBaseUrl,
            new Uri(GateConnector.DefaultWsUrl),
            wsTransportFactory: () => throw new InvalidOperationException(),
            credentials: new GateCredentials("test-key", "test-secret"),
            authInnerHandler: handler);
    }

    private static HttpResponseMessage OkJson(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    // Gate 期货下单成功返回 201
    private static HttpResponseMessage CreatedJson(string json) => new(HttpStatusCode.Created)
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
