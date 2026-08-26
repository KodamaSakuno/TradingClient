using System.Globalization;
using System.Net.Http.Json;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Bitget.Auth;
using TradingClient.Exchanges.Bitget.Models;
using TradingClient.Exchanges.Common;

namespace TradingClient.Exchanges.Bitget;

public sealed class BitgetConnector : ExchangeConnectorBase, IMarketData, IAccountService, IAsyncDisposable
{
    public const string DefaultBaseUrl = "https://api.bitget.com";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ServerTimeSync _timeSync = new();
    private readonly BitgetCredentials? _credentials;
    private readonly bool _demoTrading;
    // 测试注入点：鉴权链路的内层 handler 桩，生产为 null（BitgetAuthHandler 默认 HttpClientHandler）
    private readonly HttpMessageHandler? _authInnerHandler;

    private HttpClient? _authenticatedHttpClient;

    public BitgetConnector(HttpClient httpClient, string baseUrl = DefaultBaseUrl, BitgetCredentials? credentials = null, bool demoTrading = false)
        : this(httpClient, baseUrl, credentials, demoTrading, authInnerHandler: null)
    {
    }

    internal BitgetConnector(
        HttpClient httpClient,
        string baseUrl,
        BitgetCredentials? credentials,
        bool demoTrading,
        HttpMessageHandler? authInnerHandler = null)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _credentials = credentials;
        _demoTrading = demoTrading;
        _authInnerHandler = authInnerHandler;
    }

    public override string ExchangeId => "Bitget";

    // 供测试断言 ConnectAsync 的校时结果
    internal ServerTimeSync TimeSync => _timeSync;

    // 与 Gate Classic 构成对比象限（§11）：统一账户、无需账户内划转
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
            throw new NotSupportedException($"Bitget {product} instruments are not supported yet.");

        var response = await _httpClient.GetFromJsonAsync(
            $"{_baseUrl}/api/v3/market/instruments?category=SPOT",
            BitgetJsonContext.Default.BitgetResponseBitgetInstrumentArray, ct);

        return response?.Data?.Select(ToInstrument).ToArray() ?? [];
    }

    public IObservable<Quote> SubscribeQuotes(Symbol symbol) => throw new NotImplementedException();

    public IObservable<Trade> SubscribeTrades(Symbol symbol) => throw new NotImplementedException();

    public IObservable<OrderBookDelta> SubscribeOrderBook(Symbol symbol) => throw new NotImplementedException();

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

    public ValueTask DisposeAsync()
    {
        _authenticatedHttpClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static Instrument ToInstrument(BitgetInstrument dto)
    {
        // Reality 币 baseCoin 为混合大小写（如 "rPBR"），symbol 又是无分隔符拼接、无法可靠切分，
        // 故不走 BitgetSymbolFormatter.ParseSpot，直接用 baseCoin/quoteCoin 字段构造（§4.1）
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
