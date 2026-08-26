using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Common;
using TradingClient.Exchanges.Gate.Auth;
using TradingClient.Exchanges.Gate.Models;
using TradingClient.Exchanges.Gate.WebSocket;

namespace TradingClient.Exchanges.Gate;

public sealed class GateConnector : ExchangeConnectorBase, IMarketData, IAccountService, ISpotTrading, IAsyncDisposable
{
    public const string DefaultBaseUrl = "https://api.gateio.ws";
    public const string DefaultWsUrl = "wss://api.gateio.ws/ws/v4/";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly GateSpotWsClient _wsClient;
    private readonly ServerTimeSync _timeSync = new();
    private readonly GateCredentials? _credentials;
    // Gate 现货下单+改单合计限频 10r/s，令牌桶宁保守勿激进（§7）
    private readonly TokenBucketRateLimiter _spotRateLimiter;
    // 测试注入点：鉴权链路的内层 handler 桩，生产为 null（GateAuthHandler 默认 HttpClientHandler）
    private readonly HttpMessageHandler? _authInnerHandler;

    private HttpClient? _authenticatedHttpClient;

    public GateConnector(HttpClient httpClient, string baseUrl = DefaultBaseUrl, GateCredentials? credentials = null, string? wsUrl = null, IWebProxy? wsProxy = null)
        : this(httpClient, baseUrl, new Uri(wsUrl ?? DefaultWsUrl), () => new ClientWebSocketTransport(wsProxy), credentials: credentials)
    {
    }

    internal GateConnector(
        HttpClient httpClient,
        string baseUrl,
        Uri wsEndpoint,
        Func<IWsTransport> wsTransportFactory,
        TimeSpan? wsPingInterval = null,
        GateCredentials? credentials = null,
        HttpMessageHandler? authInnerHandler = null,
        TokenBucketRateLimiter? spotRateLimiter = null)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _credentials = credentials;
        _authInnerHandler = authInnerHandler;
        _spotRateLimiter = spotRateLimiter ?? new TokenBucketRateLimiter(capacity: 10, refillPerSecond: 10);
        _wsClient = new GateSpotWsClient(wsEndpoint, wsTransportFactory, SetConnectionState, ReconnectAsync, wsPingInterval, credentials, _timeSync);
    }

    public override string ExchangeId => "Gate";

    // 供测试断言 ConnectAsync 的校时结果
    internal ServerTimeSync TimeSync => _timeSync;

    public override ExchangeCapabilities Capabilities { get; } = new(
        AccountMode.Classic,
        RequiresInternalTransfers: true,
        Products: [ProductKind.Spot]);

    public override async Task ConnectAsync(CancellationToken ct)
    {
        await SyncServerTimeAsync(ct);
        SetConnectionState(ConnectionState.Connected);
    }

    // 用公共接口校准服务器时间；失败时降级为本地时钟（签名时间戳有 60 秒容差），不阻止连接
    // TODO: 校准失败需记结构化日志（Serilog 尚未接入本层）
    private async Task SyncServerTimeAsync(CancellationToken ct)
    {
        try
        {
            var serverTime = await _httpClient.GetFromJsonAsync(
                $"{_baseUrl}/api/v4/spot/time",
                GateJsonContext.Default.GateServerTime, ct);

            if (serverTime is not null)
                _timeSync.Update(DateTimeOffset.FromUnixTimeMilliseconds(serverTime.ServerTime));
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
            throw new InvalidOperationException("Gate authenticated endpoints require credentials.");

        var handler = new GateAuthHandler(_credentials, _timeSync);
        // 生产路径无桩注入时必须显式指定真实内层 handler，否则 DelegatingHandler 在发送时抛 InvalidOperationException
        handler.InnerHandler = _authInnerHandler ?? new HttpClientHandler();

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(_baseUrl + "/"),
        };
    }

    private HttpClient AuthenticatedHttpClient => _authenticatedHttpClient ??= CreateAuthenticatedHttpClient();

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(ProductKind product, CancellationToken ct)
    {
        if (product != ProductKind.Spot)
            throw new NotSupportedException($"Gate {product} instruments are not supported yet.");

        var pairs = await _httpClient.GetFromJsonAsync(
            $"{_baseUrl}/api/v4/spot/currency_pairs",
            GateJsonContext.Default.GateCurrencyPairArray, ct);

        return pairs?.Select(ToInstrument).ToArray() ?? [];
    }

    public IObservable<Quote> SubscribeQuotes(Symbol symbol) => _wsClient.SubscribeQuotes(RequireSpot(symbol));

    public IObservable<Trade> SubscribeTrades(Symbol symbol) => _wsClient.SubscribeTrades(RequireSpot(symbol));

    public IObservable<OrderBookDelta> SubscribeOrderBook(Symbol symbol) => _wsClient.SubscribeOrderBook(RequireSpot(symbol));

    public IObservable<Candle> SubscribeCandles(Symbol symbol, TimeFrame tf) => throw new NotImplementedException();

    public async Task<Result<AccountSummary>> GetAccountAsync(CancellationToken ct)
    {
        if (_credentials is null)
            return Result.Failure<AccountSummary>(new ExchangeError(
                "MISSING_CREDENTIALS", "Gate authenticated endpoints require credentials."));

        using var response = await AuthenticatedHttpClient.GetAsync("api/v4/spot/accounts", ct);
        if (!response.IsSuccessStatusCode)
            return Result.Failure<AccountSummary>(await GateErrorMapper.FromResponseAsync(response, ct));

        var accounts = await response.Content.ReadFromJsonAsync(GateJsonContext.Default.GateSpotAccountArray, ct)
            ?? [];

        var assets = accounts.Select(a =>
        {
            var available = decimal.Parse(a.Available, CultureInfo.InvariantCulture);
            var locked = decimal.Parse(a.Locked, CultureInfo.InvariantCulture);
            var total = available + locked;
            // EquityValue 未折算：跨币种折算需要行情价格，且本地文档无 /wallet/total_balance，暂以本币总量占位
            return new AssetBalance(a.Currency, total, locked, CollateralWeight: null, EquityValue: total);
        }).ToArray();

        // 现货 Classic 无保证金概念，且 TotalEquity 需跨币种折算（同上），权益与保证金字段一律为 0
        return Result.Success(new AccountSummary(
            AccountMode.Classic,
            TotalEquity: 0m,
            AvailableMargin: 0m,
            InitialMargin: 0m,
            MaintenanceMargin: 0m,
            MarginRatio: 0m,
            assets));
    }

    public Task<Result> TransferFundsAsync(TransferRequest req, CancellationToken ct) =>
        throw new NotImplementedException();

    public IObservable<SpotOrderUpdate> SpotOrderUpdates => _wsClient.SubscribeSpotOrderUpdates();

    public async Task<Result<SpotOrder>> PlaceSpotOrderAsync(PlaceSpotOrderRequest req, CancellationToken ct)
    {
        if (req.Quantity <= 0)
            return Result.Failure<SpotOrder>(new ExchangeError("INVALID_QUANTITY", "Quantity must be positive."));
        if (req is { Type: OrderType.Limit, Price: null })
            return Result.Failure<SpotOrder>(new ExchangeError("MISSING_PRICE", "Limit order requires a price."));
        // 决策（§7 数量语义）：领域 Quantity 统一为 base 币数量，而 Gate market buy 的 amount 是 quote 币金额，
        // 不经行情换算无法映射，本步直接拒单；limit 买卖与 market sell（amount 为 base 数量）正常下单
        if (req is { Type: OrderType.Market, Side: OrderSide.Buy })
            return Result.Failure<SpotOrder>(new ExchangeError(
                "UNSUPPORTED_ORDER",
                "Gate market buy amount is a quote-currency value; domain Quantity is a base-currency quantity. Conversion requires market data and is not supported yet."));
        if (_credentials is null)
            return Result.Failure<SpotOrder>(new ExchangeError(
                "MISSING_CREDENTIALS", "Gate authenticated endpoints require credentials."));

        await _spotRateLimiter.WaitAsync(ct);

        // market 单 Gate 只支持 ioc/fok，本步统一 ioc；limit 默认 gtc
        var body = new GateSpotOrderRequest(
            GateSymbolFormatter.FormatSpot(RequireSpot(req.Symbol)),
            req.Type == OrderType.Limit ? "limit" : "market",
            req.Side == OrderSide.Buy ? "buy" : "sell",
            req.Quantity.ToString(CultureInfo.InvariantCulture),
            req.Price?.ToString(CultureInfo.InvariantCulture),
            req.Type == OrderType.Limit ? "gtc" : "ioc");

        using var response = await AuthenticatedHttpClient.PostAsJsonAsync(
            "api/v4/spot/orders", body, GateJsonContext.Default.GateSpotOrderRequest, ct);
        if (!response.IsSuccessStatusCode)
            return Result.Failure<SpotOrder>(await GateErrorMapper.FromResponseAsync(response, ct));

        var order = await response.Content.ReadFromJsonAsync(GateJsonContext.Default.GateSpotOrder, ct);
        return Result.Success(ToSpotOrder(order!));
    }

    public async Task<Result> CancelSpotOrderAsync(Symbol symbol, string orderId, CancellationToken ct)
    {
        if (_credentials is null)
            return Result.Failure(new ExchangeError(
                "MISSING_CREDENTIALS", "Gate authenticated endpoints require credentials."));

        await _spotRateLimiter.WaitAsync(ct);

        var pair = GateSymbolFormatter.FormatSpot(RequireSpot(symbol));
        using var response = await AuthenticatedHttpClient.DeleteAsync(
            $"api/v4/spot/orders/{Uri.EscapeDataString(orderId)}?currency_pair={pair}", ct);
        if (!response.IsSuccessStatusCode)
            return Result.Failure(await GateErrorMapper.FromResponseAsync(response, ct));

        return Result.Success();
    }

    public async ValueTask DisposeAsync()
    {
        _authenticatedHttpClient?.Dispose();
        await _wsClient.DisposeAsync();
    }

    private static SpotSymbol RequireSpot(Symbol symbol) =>
        symbol as SpotSymbol
        ?? throw new NotSupportedException($"Gate spot market data does not support symbol type {symbol.GetType().Name}.");

    private static SpotOrder ToSpotOrder(GateSpotOrder dto)
    {
        var quantity = decimal.Parse(dto.Amount, CultureInfo.InvariantCulture);
        var left = decimal.Parse(dto.Left, CultureInfo.InvariantCulture);
        var filled = quantity - left;
        var type = dto.Type == "market" ? OrderType.Market : OrderType.Limit;

        // closed 即全部成交；cancelled 无论是否部分成交都归入 Cancelled（部分成交量体现在 FilledQuantity）
        var status = dto.Status switch
        {
            "open" => filled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.New,
            "closed" => OrderStatus.Filled,
            "cancelled" => OrderStatus.Cancelled,
            // Gate 文档状态枚举仅 open/closed/cancelled；出现未知值视为协议漂移（系统故障），不走 Result
            _ => throw new NotSupportedException($"Unknown Gate spot order status '{dto.Status}'."),
        };

        return new SpotOrder(
            dto.Id,
            GateSymbolFormatter.ParseSpot(dto.CurrencyPair),
            dto.Side == "buy" ? OrderSide.Buy : OrderSide.Sell,
            type,
            // market 单领域语义 Price=null；Gate 对 market 单可能返回 "0"
            type == OrderType.Market || dto.Price is null
                ? null
                : decimal.Parse(dto.Price, CultureInfo.InvariantCulture),
            quantity,
            filled,
            status,
            DateTimeOffset.FromUnixTimeMilliseconds(dto.CreateTimeMs));
    }

    private static Instrument ToInstrument(GateCurrencyPair pair) =>
        new(
            GateSymbolFormatter.ParseSpot(pair.Id),
            TickSize: Pow10Negative(pair.Precision),
            StepSize: Pow10Negative(pair.AmountPrecision),
            MinQuantity: pair.MinBaseAmount is null
                ? 0m
                : decimal.Parse(pair.MinBaseAmount, CultureInfo.InvariantCulture),
            MinQuoteAmount: pair.MinQuoteAmount is null
                ? null
                : decimal.Parse(pair.MinQuoteAmount, CultureInfo.InvariantCulture),
            ContractMultiplier: null,
            Status: pair.TradeStatus == "tradable" ? InstrumentStatus.Trading : InstrumentStatus.Suspended);

    private static decimal Pow10Negative(int precision)
    {
        var value = 1m;
        for (var i = 0; i < precision; i++)
            value /= 10m;
        return value;
    }
}
