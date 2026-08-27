using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Common;
using TradingClient.Exchanges.Gate.Auth;
using TradingClient.Exchanges.Gate.Models;
using TradingClient.Exchanges.Gate.WebSocket;

namespace TradingClient.Exchanges.Gate;

public sealed class GateConnector : ExchangeConnectorBase, IMarketData, IAccountService, ISpotTrading, IFuturesTrading, IAsyncDisposable
{
    public const string DefaultBaseUrl = "https://api.gateio.ws";
    public const string DefaultWsUrl = "wss://api.gateio.ws/ws/v4/";
    // 永续合约 WS 实盘端点（usdt settle）；testnet 是 wss://ws-testnet.gate.com/v4/ws/futures/usdt，与现货 testnet 不同路径，由调用方传入
    public const string DefaultFuturesWsUrl = "wss://fx-ws.gateio.ws/v4/ws/usdt";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly GateSpotWsClient _wsClient;
    private readonly GateFuturesWsClient _futuresWsClient;
    private readonly ServerTimeSync _timeSync = new();
    private readonly GateCredentials? _credentials;
    // Gate 现货下单+改单合计限频 10r/s，令牌桶宁保守勿激进（§7）
    private readonly TokenBucketRateLimiter _spotRateLimiter;
    // Gate 期货下单+改单合计限频 100r/s/UID（撤单 200r/s，保守共用 100r/s 桶）
    private readonly TokenBucketRateLimiter _futuresRateLimiter;
    // 测试注入点：鉴权链路的内层 handler 桩，生产为 null（GateAuthHandler 默认 HttpClientHandler）
    private readonly HttpMessageHandler? _authInnerHandler;
    // 张→币换算（§7）所需的 quanto_multiplier 缓存：合约名（如 BTC_USDT）→ 乘数，拉 contracts 时顺手填充
    private readonly ConcurrentDictionary<string, decimal> _futuresQuantoMultipliers = new();
    private readonly SemaphoreSlim _futuresContractsLock = new(1, 1);
    // 持仓模式是账户级状态：本地缓存供下单映射（reduce_only 分支）用；站外改模式（网页/App）会失准
    private PositionMode _positionMode = PositionMode.Single;

    private HttpClient? _authenticatedHttpClient;

    public GateConnector(HttpClient httpClient, string baseUrl = DefaultBaseUrl, GateCredentials? credentials = null, string? wsUrl = null, IWebProxy? wsProxy = null, string? futuresWsUrl = null)
        : this(httpClient, baseUrl, new Uri(wsUrl ?? DefaultWsUrl), () => new ClientWebSocketTransport(wsProxy), credentials: credentials,
            futuresWsEndpoint: new Uri(futuresWsUrl ?? DefaultFuturesWsUrl))
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
        TokenBucketRateLimiter? spotRateLimiter = null,
        TokenBucketRateLimiter? futuresRateLimiter = null,
        Uri? futuresWsEndpoint = null,
        Func<IWsTransport>? futuresWsTransportFactory = null)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _credentials = credentials;
        _authInnerHandler = authInnerHandler;
        _spotRateLimiter = spotRateLimiter ?? new TokenBucketRateLimiter(capacity: 10, refillPerSecond: 10);
        _futuresRateLimiter = futuresRateLimiter ?? new TokenBucketRateLimiter(capacity: 100, refillPerSecond: 100);
        _wsClient = new GateSpotWsClient(wsEndpoint, wsTransportFactory, SetConnectionState, ReconnectAsync, wsPingInterval, credentials, _timeSync);
        _futuresWsClient = new GateFuturesWsClient(
            futuresWsEndpoint ?? new Uri(DefaultFuturesWsUrl),
            futuresWsTransportFactory ?? wsTransportFactory,
            SetConnectionState,
            ReconnectAsync,
            GetQuantoMultiplier,
            wsPingInterval,
            credentials,
            _timeSync);
    }

    public override string ExchangeId => "Gate";

    // 供测试断言 ConnectAsync 的校时结果
    internal ServerTimeSync TimeSync => _timeSync;

    public override ExchangeCapabilities Capabilities { get; } = new(
        AccountMode.Classic,
        RequiresInternalTransfers: true,
        Products: [ProductKind.Spot, ProductKind.Futures],
        SupportsDualPositionMode: true);

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
        return product switch
        {
            ProductKind.Spot => await GetSpotInstrumentsAsync(ct),
            ProductKind.Futures => await GetFuturesInstrumentsAsync(ct),
            _ => throw new NotSupportedException($"Gate {product} instruments are not supported yet."),
        };
    }

    private async Task<IReadOnlyList<Instrument>> GetSpotInstrumentsAsync(CancellationToken ct)
    {
        var pairs = await _httpClient.GetFromJsonAsync(
            $"{_baseUrl}/api/v4/spot/currency_pairs",
            GateJsonContext.Default.GateCurrencyPairArray, ct);

        return pairs?.Select(ToInstrument).ToArray() ?? [];
    }

    // 阶段 3 只接 usdt 结算（fixture 录自 testnet 2026-08-27）；btc/usd1 settle 未接
    private async Task<IReadOnlyList<Instrument>> GetFuturesInstrumentsAsync(CancellationToken ct) =>
        (await LoadFuturesContractsAsync(ct)).Select(ToFuturesInstrument).ToArray();

    // 拉全量合约并顺手填充张→币乘数缓存，供期货 WS 推送换算（§7）
    private async Task<GateFuturesContract[]> LoadFuturesContractsAsync(CancellationToken ct)
    {
        var contracts = await _httpClient.GetFromJsonAsync(
            $"{_baseUrl}/api/v4/futures/usdt/contracts",
            GateJsonContext.Default.GateFuturesContractArray, ct) ?? [];

        foreach (var contract in contracts)
            _futuresQuantoMultipliers[contract.Name] = decimal.Parse(contract.QuantoMultiplier, CultureInfo.InvariantCulture);

        return contracts;
    }

    // 期货 WS 订阅要求乘数缓存就绪（WS 层拿不到 Instrument）；首次订阅时缓存为空则先补拉一次 contracts
    private async Task EnsureFuturesContractsCachedAsync(CancellationToken ct)
    {
        if (!_futuresQuantoMultipliers.IsEmpty)
            return;

        await _futuresContractsLock.WaitAsync(ct);
        try
        {
            if (_futuresQuantoMultipliers.IsEmpty)
                await LoadFuturesContractsAsync(ct);
        }
        finally
        {
            _futuresContractsLock.Release();
        }
    }

    // 注入期货 WS client 的乘数查询；查不到（未知合约/缓存未就绪）抛 NotSupportedException，由订阅管线当坏消息跳过该帧
    internal decimal GetQuantoMultiplier(string contractName) =>
        _futuresQuantoMultipliers.TryGetValue(contractName, out var multiplier)
            ? multiplier
            : throw new NotSupportedException(
                $"Unknown Gate futures contract '{contractName}': quanto multiplier is not cached.");

    public IObservable<Quote> SubscribeQuotes(Symbol symbol) => symbol switch
    {
        SpotSymbol spot => _wsClient.SubscribeQuotes(spot),
        PerpetualFuturesSymbol perp => SubscribeFutures(perp, _futuresWsClient.SubscribeQuotes),
        _ => throw UnsupportedSymbol(symbol),
    };

    public IObservable<Trade> SubscribeTrades(Symbol symbol) => symbol switch
    {
        SpotSymbol spot => _wsClient.SubscribeTrades(spot),
        PerpetualFuturesSymbol perp => SubscribeFutures(perp, _futuresWsClient.SubscribeTrades),
        _ => throw UnsupportedSymbol(symbol),
    };

    public IObservable<OrderBookDelta> SubscribeOrderBook(Symbol symbol) => symbol switch
    {
        SpotSymbol spot => _wsClient.SubscribeOrderBook(spot),
        PerpetualFuturesSymbol perp => SubscribeFutures(perp, _futuresWsClient.SubscribeOrderBook),
        _ => throw UnsupportedSymbol(symbol),
    };

    // 订阅动作发生时先确保乘数缓存就绪，再进入 WS 订阅
    private IObservable<T> SubscribeFutures<T>(
        PerpetualFuturesSymbol symbol, Func<PerpetualFuturesSymbol, IObservable<T>> subscribe) =>
        Observable.FromAsync(EnsureFuturesContractsCachedAsync).SelectMany(_ => subscribe(symbol));

    // 交割合约是另一族端点（本刀不接），OptionSymbol 等其余类型同样不受支持
    private static NotSupportedException UnsupportedSymbol(Symbol symbol) =>
        new($"Gate market data does not support symbol type {symbol.GetType().Name}.");

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

    // 期货私有 WS 推送：订 futures.positions 前确保张→币乘数缓存就绪（订阅动作发生时执行）
    public IObservable<PositionUpdate> PositionUpdates =>
        Observable.FromAsync(EnsureFuturesContractsCachedAsync)
            .SelectMany(_ => _futuresWsClient.SubscribePositionUpdates());

    // Gate 无事前强平预警频道且推送的 liq_price 已废弃，预警由持仓推送本地线性估算（协议层注释有完整说明）；
    // 与 PositionUpdates 共用同一条 futures.positions 订阅（client 引用计数去重）
    public IObservable<LiquidationWarning> LiquidationWarnings =>
        Observable.FromAsync(EnsureFuturesContractsCachedAsync)
            .SelectMany(_ => _futuresWsClient.SubscribeLiquidationWarnings());

    public async Task<Result<FuturesOrder>> PlaceFuturesOrderAsync(PlaceFuturesOrderRequest req, CancellationToken ct)
    {
        if (req.Quantity <= 0)
            return Result.Failure<FuturesOrder>(new ExchangeError("INVALID_QUANTITY", "Quantity must be positive."));
        if (req is { Type: OrderType.Limit, Price: null })
            return Result.Failure<FuturesOrder>(new ExchangeError("MISSING_PRICE", "Limit order requires a price."));
        var perp = RequirePerpetual(req.Symbol);
        if (_credentials is null)
            return Result.Failure<FuturesOrder>(new ExchangeError(
                "MISSING_CREDENTIALS", "Gate authenticated endpoints require credentials."));

        var contract = GateSymbolFormatter.FormatFutures(perp);
        await EnsureFuturesContractsCachedAsync(ct);
        var multiplier = GetQuantoMultiplier(contract);

        // 币→张换算（§7 数量语义）：本层是兜底校验，用例层已按 StepSize=1张×multiplier 对齐（§4.2）。
        // enable_decimal=false 张数为整数；不整除与张数超 long 范围同属 INVALID_QUANTITY
        var contractsDecimal = req.Quantity / multiplier;
        if (contractsDecimal != decimal.Truncate(contractsDecimal) || contractsDecimal > long.MaxValue)
            return Result.Failure<FuturesOrder>(new ExchangeError(
                "INVALID_QUANTITY",
                $"Quantity {req.Quantity} is not a whole number of contracts (quanto multiplier {multiplier})."));

        var contracts = (long)contractsDecimal;

        // 下单 size/reduce_only 按持仓模式分支（协议约定出自 Place futures order 文档）：
        // single：size 带符号，正=买/开多、负=卖/开空，反向单即平仓，不带 reduce_only
        // dual：加仓 size 正=加多/负=加空；减仓 reduce_only=true，size 正（买）=减空、负（卖）=减多。
        // 全平的 size=0 + auto_size=close_long/close_short 形态不接，用 reduce_only + 显式数量即可
        var signedSize = req.Side == OrderSide.Buy ? contracts : -contracts;
        bool? reduceOnly = null;
        if (_positionMode == PositionMode.Dual)
        {
            (signedSize, reduceOnly) = (req.PositionSide, req.Side) switch
            {
                (PositionSide.Long, OrderSide.Buy) => (contracts, (bool?)null),
                (PositionSide.Short, OrderSide.Sell) => (-contracts, (bool?)null),
                (PositionSide.Long, OrderSide.Sell) => (-contracts, (bool?)true),
                (PositionSide.Short, OrderSide.Buy) => (contracts, (bool?)true),
                // dual 下 Both 无目标腿，属编程错误而非业务失败
                _ => throw new ArgumentException(
                    $"Dual position mode requires PositionSide Long or Short, got {req.PositionSide}.", nameof(req)),
            };
        }

        // Gate 杠杆挂在持仓维度而非订单维度（协议形态）：req.Leverage 有值时先 set_leverage 再下单；
        // 为 null 则不动账户当前杠杆
        if (req.Leverage is { } leverage)
        {
            var leverageResult = await SendSetLeverageAsync(contract, leverage, req.MarginMode, ct);
            if (!leverageResult.IsSuccess)
                return Result.Failure<FuturesOrder>(leverageResult.Error!);
        }

        await _futuresRateLimiter.WaitAsync(ct);

        var body = new GateFuturesOrderRequest(
            contract,
            signedSize,
            // 市价单协议形态：price "0" + tif ioc；限价单默认 gtc
            req.Type == OrderType.Limit ? req.Price!.Value.ToString(CultureInfo.InvariantCulture) : "0",
            req.Type == OrderType.Limit ? "gtc" : "ioc",
            reduceOnly);

        using var response = await AuthenticatedHttpClient.PostAsJsonAsync(
            "api/v4/futures/usdt/orders", body, GateJsonContext.Default.GateFuturesOrderRequest, ct);
        if (!response.IsSuccessStatusCode)
            return Result.Failure<FuturesOrder>(await GateErrorMapper.FromResponseAsync(response, ct));

        var order = await response.Content.ReadFromJsonAsync(GateJsonContext.Default.GateFuturesOrder, ct);
        return Result.Success(ToFuturesOrder(order!, req.PositionSide, req.MarginMode, multiplier));
    }

    public async Task<Result> SetLeverageAsync(Symbol symbol, int leverage, MarginMode mode, CancellationToken ct)
    {
        var perp = RequirePerpetual(symbol);
        if (_credentials is null)
            return Result.Failure(new ExchangeError(
                "MISSING_CREDENTIALS", "Gate authenticated endpoints require credentials."));

        return await SendSetLeverageAsync(GateSymbolFormatter.FormatFutures(perp), leverage, mode, ct);
    }

    // 持仓模式为账户级开关（无合约维度）；dual_plus（split position）不接
    public async Task<Result> SetPositionModeAsync(PositionMode mode, CancellationToken ct)
    {
        if (_credentials is null)
            return Result.Failure(new ExchangeError(
                "MISSING_CREDENTIALS", "Gate authenticated endpoints require credentials."));

        await _futuresRateLimiter.WaitAsync(ct);

        using var response = await AuthenticatedHttpClient.PostAsync(
            $"api/v4/futures/usdt/set_position_mode?position_mode={(mode == PositionMode.Dual ? "dual" : "single")}",
            content: null, ct);
        if (!response.IsSuccessStatusCode)
            return Result.Failure(await GateErrorMapper.FromResponseAsync(response, ct));

        _positionMode = mode;
        return Result.Success();
    }

    // 新接口（margin_mode 显式指定全/逐仓）；旧接口 leverage=0 表全仓是语义陷阱，不用
    private async Task<Result> SendSetLeverageAsync(string contract, int leverage, MarginMode mode, CancellationToken ct)
    {
        var marginMode = mode switch
        {
            MarginMode.Cross => "cross",
            MarginMode.Isolated => "isolated",
            // PortfolioMargin 为 Domain 预留枚举值，Gate 无对应模式，属编程错误而非业务失败
            _ => throw new ArgumentException($"Gate does not support margin mode {mode}.", nameof(mode)),
        };

        await _futuresRateLimiter.WaitAsync(ct);

        using var response = await AuthenticatedHttpClient.PostAsync(
            $"api/v4/futures/usdt/positions/{contract}/set_leverage?leverage={leverage}&margin_mode={marginMode}",
            content: null, ct);
        if (!response.IsSuccessStatusCode)
            return Result.Failure(await GateErrorMapper.FromResponseAsync(response, ct));

        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<Position>>> GetPositionsAsync(CancellationToken ct)
    {
        if (_credentials is null)
            return Result.Failure<IReadOnlyList<Position>>(new ExchangeError(
                "MISSING_CREDENTIALS", "Gate authenticated endpoints require credentials."));

        await EnsureFuturesContractsCachedAsync(ct);

        // holding=true 只返回实际持仓
        using var response = await AuthenticatedHttpClient.GetAsync("api/v4/futures/usdt/positions?holding=true", ct);
        if (!response.IsSuccessStatusCode)
            return Result.Failure<IReadOnlyList<Position>>(await GateErrorMapper.FromResponseAsync(response, ct));

        var dtos = await response.Content.ReadFromJsonAsync(GateJsonContext.Default.GateFuturesPositionArray, ct) ?? [];
        return Result.Success<IReadOnlyList<Position>>(dtos.Select(ToPosition).ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _authenticatedHttpClient?.Dispose();
        await _wsClient.DisposeAsync();
        await _futuresWsClient.DisposeAsync();
        _futuresContractsLock.Dispose();
    }

    private static SpotSymbol RequireSpot(Symbol symbol) =>
        symbol as SpotSymbol
        ?? throw new NotSupportedException($"Gate spot trading does not support symbol type {symbol.GetType().Name}.");

    // 本刀只接 usdt 永续；交割合约是另一族端点（/delivery/...），不接
    private static PerpetualFuturesSymbol RequirePerpetual(Symbol symbol) =>
        symbol as PerpetualFuturesSymbol
        ?? throw new NotSupportedException($"Gate futures trading does not support symbol type {symbol.GetType().Name}.");

    private static FuturesOrder ToFuturesOrder(GateFuturesOrder dto, PositionSide positionSide, MarginMode marginMode, decimal multiplier)
    {
        var size = Math.Abs(dto.Size);
        var filled = size - Math.Abs(dto.Left);
        // 市价单协议形态 price "0" → 领域 Price=null（Gate 现货同款处理）
        var isMarket = dto.Price is null or "0";

        // status 两态：open 按 left 细分；finished 看 finish_as，ioc/liquidated 等非 filled 一律归 Cancelled（部分成交体现在 FilledQuantity）
        var status = dto.Status switch
        {
            "open" => filled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.New,
            "finished" => dto.FinishAs switch
            {
                "filled" => OrderStatus.Filled,
                "cancelled" or "liquidated" or "ioc" or "auto_deleveraged" or "reduce_only"
                    or "position_closed" or "reduce_out" or "stp" => OrderStatus.Cancelled,
                // 未知 finish_as 视为协议漂移（系统故障），不走 Result
                _ => throw new NotSupportedException($"Unknown Gate futures order finish_as '{dto.FinishAs}'."),
            },
            _ => throw new NotSupportedException($"Unknown Gate futures order status '{dto.Status}'."),
        };

        var side = dto.Size > 0 ? OrderSide.Buy : OrderSide.Sell;
        return new FuturesOrder(
            dto.Id.ToString(CultureInfo.InvariantCulture),
            GateSymbolFormatter.ParseFutures(dto.Contract),
            side,
            isMarket ? OrderType.Market : OrderType.Limit,
            isMarket ? null : decimal.Parse(dto.Price!, CultureInfo.InvariantCulture),
            size * multiplier,
            filled * multiplier,
            status,
            // PositionSide 从请求传入而非按 size 符号推导：dual 减持单方向与目标腿相反（Buy 减空 → Short）
            positionSide,
            marginMode,
            // create_time 为秒（可小数）
            DateTimeOffset.FromUnixTimeMilliseconds((long)(dto.CreateTime * 1000)));
    }

    private Position ToPosition(GateFuturesPosition dto)
    {
        var multiplier = GetQuantoMultiplier(dto.Contract);
        // leverage "0"=全仓（实际杠杆上限看 cross_leverage_limit），非 0=逐仓杠杆
        var isCross = dto.Leverage == "0";
        var leverage = isCross
            ? int.TryParse(dto.CrossLeverageLimit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var crossLimit)
                ? crossLimit
                : 0 // cross_leverage_limit 解析失败时取 0（未知），不阻断持仓映射
            : int.Parse(dto.Leverage, CultureInfo.InvariantCulture);

        return new Position(
            GateSymbolFormatter.ParseFutures(dto.Contract),
            // dual 模式按 mode 字段定腿；single 及其他按 size 符号（WS 侧同款映射见 GateFuturesWsClient）
            dto.Mode switch
            {
                "dual_long" => PositionSide.Long,
                "dual_short" => PositionSide.Short,
                _ => dto.Size > 0 ? PositionSide.Long : PositionSide.Short,
            },
            Math.Abs(dto.Size) * multiplier,
            decimal.Parse(dto.EntryPrice, CultureInfo.InvariantCulture),
            decimal.Parse(dto.UnrealisedPnl, CultureInfo.InvariantCulture),
            leverage,
            isCross ? MarginMode.Cross : MarginMode.Isolated);
    }

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

    // 数量语义（§7 铁律）：领域层统一币的数量，张数换算在此消化——
    // MinQuantity = 最小张数 × quanto_multiplier，StepSize = 1 张 × quanto_multiplier
    private static Instrument ToFuturesInstrument(GateFuturesContract contract)
    {
        var multiplier = decimal.Parse(contract.QuantoMultiplier, CultureInfo.InvariantCulture);

        return new Instrument(
            GateSymbolFormatter.ParseFutures(contract.Name),
            TickSize: decimal.Parse(contract.OrderPriceRound, CultureInfo.InvariantCulture),
            // enable_decimal=true 的小数张精度文档未给出，保守按 1 张步长（§7：宁保守勿激进）
            StepSize: multiplier,
            MinQuantity: contract.OrderSizeMin * multiplier,
            MinQuoteAmount: null,
            ContractMultiplier: multiplier,
            // in_delisting=true 为下架过渡期/已下架，即便 status=trading 也归 Suspended
            Status: contract.Status == "trading" && !contract.InDelisting
                ? InstrumentStatus.Trading
                : InstrumentStatus.Suspended);
    }

    private static decimal Pow10Negative(int precision)
    {
        var value = 1m;
        for (var i = 0; i < precision; i++)
            value /= 10m;
        return value;
    }
}
