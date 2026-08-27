using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Bitget.Auth;
using TradingClient.Exchanges.Bitget.Models;
using TradingClient.Exchanges.Bitget.WebSocket;
using TradingClient.Exchanges.Common;

namespace TradingClient.Exchanges.Bitget;

public sealed class BitgetConnector : ExchangeConnectorBase, IMarketData, IAccountService, ISpotTrading, IAsyncDisposable
{
    public const string DefaultBaseUrl = "https://api.bitget.com";
    // 文档不一致：rest-api.md 的表写 /v2/ws/public，quick-start.md 与模拟盘 wspap 均为 /v3，以 quick-start 为准
    public const string DefaultWsUrl = "wss://ws.bitget.com/v3/ws/public";
    public const string DemoWsUrl = "wss://wspap.bitget.com/v3/ws/public";
    // 私有频道与公共频道是两个独立端点（模拟盘为 wspap 域名）
    public const string DefaultPrivateWsUrl = "wss://ws.bitget.com/v3/ws/private";
    public const string DemoPrivateWsUrl = "wss://wspap.bitget.com/v3/ws/private";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly BitgetSpotWsClient _wsClient;
    private readonly BitgetSpotWsClient _privateWsClient;
    private readonly ServerTimeSync _timeSync = new();
    private readonly BitgetCredentials? _credentials;
    private readonly bool _demoTrading;
    // 下单/撤单限频各 10次/秒/UID，共用一个令牌桶宁保守勿激进
    private readonly TokenBucketRateLimiter _spotRateLimiter;
    // 测试注入点：鉴权链路的内层 handler 桩，生产为 null（BitgetAuthHandler 默认 HttpClientHandler）
    private readonly HttpMessageHandler? _authInnerHandler;
    // 部分网络环境访问 api.bitget.com 需代理（REST 签名链路）
    private readonly IWebProxy? _httpProxy;

    private HttpClient? _authenticatedHttpClient;

    public BitgetConnector(
        HttpClient httpClient,
        string baseUrl = DefaultBaseUrl,
        BitgetCredentials? credentials = null,
        bool demoTrading = false,
        string? wsUrl = null,
        IWebProxy? wsProxy = null,
        string? privateWsUrl = null,
        IWebProxy? httpProxy = null)
        : this(httpClient, baseUrl,
            new Uri(wsUrl ?? (demoTrading ? DemoWsUrl : DefaultWsUrl)),
            () => new ClientWebSocketTransport(wsProxy),
            credentials, demoTrading,
            privateWsEndpoint: new Uri(privateWsUrl ?? (demoTrading ? DemoPrivateWsUrl : DefaultPrivateWsUrl)),
            authInnerHandler: null)
    {
        _httpProxy = httpProxy;
    }

    internal BitgetConnector(
        HttpClient httpClient,
        string baseUrl,
        Uri wsEndpoint,
        Func<IWsTransport> wsTransportFactory,
        BitgetCredentials? credentials,
        bool demoTrading,
        TimeSpan? wsPingInterval = null,
        HttpMessageHandler? authInnerHandler = null,
        Uri? privateWsEndpoint = null,
        TokenBucketRateLimiter? spotRateLimiter = null)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _credentials = credentials;
        _demoTrading = demoTrading;
        _authInnerHandler = authInnerHandler;
        _spotRateLimiter = spotRateLimiter ?? new TokenBucketRateLimiter(capacity: 10, refillPerSecond: 10);
        _wsClient = new BitgetSpotWsClient(wsEndpoint, wsTransportFactory, SetConnectionState, ReconnectAsync, wsPingInterval);
        _privateWsClient = new BitgetSpotWsClient(
            privateWsEndpoint ?? new Uri(demoTrading ? DemoPrivateWsUrl : DefaultPrivateWsUrl),
            wsTransportFactory, SetConnectionState, ReconnectAsync, wsPingInterval, credentials, _timeSync);
    }

    public override string ExchangeId => "Bitget";

    // 供测试断言 ConnectAsync 的校时结果
    internal ServerTimeSync TimeSync => _timeSync;

    // 与 Gate Classic 构成对比象限：统一账户、无需账户内划转
    public override ExchangeCapabilities Capabilities { get; } = new(
        AccountMode.Unified,
        RequiresInternalTransfers: false,
        Products: [ProductKind.Spot]);

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await SyncServerTimeAsync(ct);
        SetConnectionState(ConnectionState.Connected);
    }

    // V3 无公共时间接口（实测 /api/v3/public/time 返回 404），校时复用同主机的 V2 接口——跨版本怪癖；
    // 失败时降级为本地时钟（签名时间戳有服务器时间 ±30 秒容差），不阻止连接
    // TODO: 校准失败需记结构化日志（Serilog 尚未接入本层）
    private async Task SyncServerTimeAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync(
                $"{_baseUrl}/api/v2/public/time",
                BitgetJsonContext.Default.BitgetResponseBitgetServerTime, ct);

            if (response?.Data?.ServerTime is { } serverTime)
                _timeSync.Update(DateTimeOffset.FromUnixTimeMilliseconds(
                    long.Parse(serverTime, CultureInfo.InvariantCulture)));
        }
        catch (Exception)
        {
            // 降级：保持本地时钟
        }
    }

    // 供账户/交易接口使用：公共行情请求不走签名，签名客户端按需单独创建并缓存复用
    internal HttpClient CreateAuthenticatedHttpClient()
    {
        if (_credentials is null)
            throw new InvalidOperationException("Bitget authenticated endpoints require credentials.");

        var handler = new BitgetAuthHandler(_credentials, _timeSync, _demoTrading);
        // 生产路径无桩注入时必须显式指定真实内层 handler，否则 DelegatingHandler 在发送时抛 InvalidOperationException
        handler.InnerHandler = _authInnerHandler
            ?? (_httpProxy is not null ? new HttpClientHandler { Proxy = _httpProxy } : new HttpClientHandler());

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl + "/"),
        };
    }

    private HttpClient AuthenticatedHttpClient => _authenticatedHttpClient ??= CreateAuthenticatedHttpClient();

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(ProductKind product, CancellationToken ct)
    {
        if (product != ProductKind.Spot)
            throw new NotSupportedException($"Bitget {product} instruments are not supported yet.");

        var response = await _httpClient.GetFromJsonAsync(
            $"{_baseUrl}/api/v3/market/instruments?category=SPOT",
            BitgetJsonContext.Default.BitgetResponseBitgetInstrumentArray, ct);

        return response?.Data?.Select(ToInstrument).ToArray() ?? [];
    }

    public IObservable<Quote> SubscribeQuotes(Symbol symbol) => _wsClient.SubscribeQuotes(RequireSpot(symbol));

    public IObservable<Trade> SubscribeTrades(Symbol symbol) => _wsClient.SubscribeTrades(RequireSpot(symbol));

    public IObservable<OrderBookDelta> SubscribeOrderBook(Symbol symbol) => _wsClient.SubscribeOrderBook(RequireSpot(symbol));

    public IObservable<Candle> SubscribeCandles(Symbol symbol, TimeFrame tf) => throw new NotImplementedException();

    public async Task<Result<AccountSummary>> GetAccountAsync(CancellationToken ct)
    {
        if (_credentials is null)
            return Result.Failure<AccountSummary>(new ExchangeError(
                "MISSING_CREDENTIALS", "Bitget authenticated endpoints require credentials."));

        using var response = await AuthenticatedHttpClient.GetAsync("api/v3/account/assets", ct);
        if (!response.IsSuccessStatusCode)
            return Result.Failure<AccountSummary>(await BitgetErrorMapper.FromResponseAsync(response, ct));

        var envelope = await response.Content.ReadFromJsonAsync(
            BitgetJsonContext.Default.BitgetResponseBitgetAccountAssets, ct);
        if (envelope?.Data is null)
            return Result.Failure<AccountSummary>(new ExchangeError(
                envelope?.Code ?? "EMPTY_DATA",
                envelope?.Msg ?? "Bitget returned empty account data."));

        var account = envelope.Data;
        var imr = Parse(account.Imr);
        var assets = account.Assets.Select(a => new AssetBalance(
                a.Coin.ToUpperInvariant(),
                Total: Parse(a.Balance),
                Frozen: Parse(a.Locked),
                // 统一账户折算率在另一 discount-rate 接口，本步未接
                CollateralWeight: null,
                EquityValue: Parse(a.UsdValue)))
            .ToArray();

        return Result.Success(new AccountSummary(
            AccountMode.Unified,
            TotalEquity: Parse(account.AccountEquity),
            // 推导口径：AvailableMargin = effEquity（可为全仓提供保证金的净值）− imr（已占用 IM）
            AvailableMargin: Parse(account.EffEquity) - imr,
            InitialMargin: imr,
            MaintenanceMargin: Parse(account.Mmr),
            MarginRatio: Parse(account.MgnRatio),
            assets));
    }

    // UTA 无需账户间划转（Capabilities.RequiresInternalTransfers=false），UI 按能力面不暴露该入口
    public Task<Result> TransferFundsAsync(TransferRequest req, CancellationToken ct) =>
        throw new NotImplementedException();

    public IObservable<SpotOrderUpdate> SpotOrderUpdates => _privateWsClient.SubscribeSpotOrderUpdates();

    public async Task<Result<SpotOrder>> PlaceSpotOrderAsync(PlaceSpotOrderRequest req, CancellationToken ct)
    {
        if (req.Quantity <= 0)
            return Result.Failure<SpotOrder>(new ExchangeError("INVALID_QUANTITY", "Quantity must be positive."));
        if (req is { Type: OrderType.Limit, Price: null })
            return Result.Failure<SpotOrder>(new ExchangeError("MISSING_PRICE", "Limit order requires a price."));
        // 决策（数量语义，与 Gate 同款处理）：领域 Quantity 统一为 base 币数量，而 Bitget 市价买单的
        // qty 是 quote 币金额，不经行情换算无法映射，直接拒单；限价单与市价卖单的 qty 均为 base 币数量
        if (req is { Type: OrderType.Market, Side: OrderSide.Buy })
            return Result.Failure<SpotOrder>(new ExchangeError(
                "UNSUPPORTED_ORDER",
                "Bitget market buy qty is a quote-currency amount; domain Quantity is a base-currency quantity. Conversion requires market data and is not supported yet."));
        if (_credentials is null)
            return Result.Failure<SpotOrder>(new ExchangeError(
                "MISSING_CREDENTIALS", "Bitget authenticated endpoints require credentials."));

        await _spotRateLimiter.WaitAsync(ct);

        var body = new BitgetPlaceOrderRequest(
            "SPOT",
            BitgetSymbolFormatter.FormatSpot(RequireSpot(req.Symbol)),
            req.Side == OrderSide.Buy ? "buy" : "sell",
            req.Type == OrderType.Limit ? "limit" : "market",
            req.Quantity.ToString(CultureInfo.InvariantCulture),
            req.Price?.ToString(CultureInfo.InvariantCulture),
            req.Type == OrderType.Limit ? "gtc" : null);

        using var response = await AuthenticatedHttpClient.PostAsJsonAsync(
            "api/v3/trade/place-order", body, BitgetJsonContext.Default.BitgetPlaceOrderRequest, ct);
        if (!response.IsSuccessStatusCode)
            return Result.Failure<SpotOrder>(await BitgetErrorMapper.FromResponseAsync(response, ct));

        var envelope = await response.Content.ReadFromJsonAsync(
            BitgetJsonContext.Default.BitgetResponseBitgetOrderAck, ct);
        // Bitget 怪癖：HTTP 200 也可能返回业务错误（code != "00000"，如 40010），信封与 HTTP 状态都要检查
        if (envelope?.Data is null || envelope.Code != "00000")
            return Result.Failure<SpotOrder>(new ExchangeError(
                envelope?.Code ?? "EMPTY_DATA",
                envelope?.Msg ?? "Bitget returned empty order data."));

        // V3 下单响应不含订单状态（仅 orderId/clientOid），成交以 order 频道推送为准：
        // 用请求参数 + 返回 ID 构造，FilledQuantity=0、Status=New
        return Result.Success(new SpotOrder(
            envelope.Data.OrderId,
            req.Symbol,
            req.Side,
            req.Type,
            req.Price,
            req.Quantity,
            FilledQuantity: 0m,
            OrderStatus.New,
            DateTimeOffset.UtcNow));
    }

    public async Task<Result> CancelSpotOrderAsync(Symbol symbol, string orderId, CancellationToken ct)
    {
        if (_credentials is null)
            return Result.Failure(new ExchangeError(
                "MISSING_CREDENTIALS", "Bitget authenticated endpoints require credentials."));

        await _spotRateLimiter.WaitAsync(ct);

        // 撤单 body 只需 orderId + category，不带 symbol；仍校验符号类型以拒绝非现货 Symbol
        _ = RequireSpot(symbol);
        var body = new BitgetCancelOrderRequest(orderId, "SPOT");
        using var response = await AuthenticatedHttpClient.PostAsJsonAsync(
            "api/v3/trade/cancel-order", body, BitgetJsonContext.Default.BitgetCancelOrderRequest, ct);
        if (!response.IsSuccessStatusCode)
            return Result.Failure(await BitgetErrorMapper.FromResponseAsync(response, ct));

        var envelope = await response.Content.ReadFromJsonAsync(
            BitgetJsonContext.Default.BitgetResponseBitgetOrderAck, ct);
        // 与下单相同：HTTP 200 下的业务错误信封也算失败
        if (envelope?.Data is null || envelope.Code != "00000")
            return Result.Failure(new ExchangeError(
                envelope?.Code ?? "EMPTY_DATA",
                envelope?.Msg ?? "Bitget returned empty cancel data."));

        return Result.Success();
    }

    public async ValueTask DisposeAsync()
    {
        _authenticatedHttpClient?.Dispose();
        await _wsClient.DisposeAsync();
        await _privateWsClient.DisposeAsync();
    }

    private static SpotSymbol RequireSpot(Symbol symbol) =>
        symbol as SpotSymbol
        ?? throw new NotSupportedException($"Bitget spot market data does not support symbol type {symbol.GetType().Name}.");

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static Instrument ToInstrument(BitgetInstrument dto)
    {
        // Reality 币 baseCoin 为混合大小写（如 "rPBR"），symbol 又是无分隔符拼接、无法可靠切分，
        // 故不走 BitgetSymbolFormatter.ParseSpot，直接用 baseCoin/quoteCoin 字段构造
        var symbol = new SpotSymbol(
            dto.BaseCoin.ToUpperInvariant(),
            dto.QuoteCoin.ToUpperInvariant());

        return new Instrument(
            symbol,
            TickSize: Pow10Negative(int.Parse(dto.PricePrecision, CultureInfo.InvariantCulture)),
            StepSize: Pow10Negative(int.Parse(dto.QuantityPrecision, CultureInfo.InvariantCulture)),
            MinQuantity: decimal.Parse(dto.MinOrderQty, CultureInfo.InvariantCulture),
            // Bitget 数值字段以字符串返回，无值时给空字符串而非 null（如 maxSymbolOrderNum），空串按 null 处理
            MinQuoteAmount: string.IsNullOrEmpty(dto.MinOrderAmount)
                ? null
                : decimal.Parse(dto.MinOrderAmount, CultureInfo.InvariantCulture),
            ContractMultiplier: null,
            Status: dto.Status == "online" ? InstrumentStatus.Trading : InstrumentStatus.Suspended);
    }

    private static decimal Pow10Negative(int precision)
    {
        var value = 1m;
        for (var i = 0; i < precision; i++)
            value /= 10m;
        return value;
    }
}
