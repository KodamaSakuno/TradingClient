using System.Globalization;
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

public sealed class GateConnector : ExchangeConnectorBase, IMarketData, IAsyncDisposable
{
    public const string DefaultBaseUrl = "https://api.gateio.ws";
    public const string DefaultWsUrl = "wss://api.gateio.ws/ws/v4/";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly GateSpotWsClient _wsClient;
    private readonly ServerTimeSync _timeSync = new();
    private readonly GateCredentials? _credentials;

    public GateConnector(HttpClient httpClient, string baseUrl = DefaultBaseUrl, GateCredentials? credentials = null)
        : this(httpClient, baseUrl, new Uri(DefaultWsUrl), () => new ClientWebSocketTransport(), credentials: credentials)
    {
    }

    internal GateConnector(
        HttpClient httpClient,
        string baseUrl,
        Uri wsEndpoint,
        Func<IGateWsTransport> wsTransportFactory,
        TimeSpan? wsPingInterval = null,
        GateCredentials? credentials = null)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _credentials = credentials;
        _wsClient = new GateSpotWsClient(wsEndpoint, wsTransportFactory, SetConnectionState, ReconnectAsync, wsPingInterval);
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

    // 供后续账户/交易接口使用：公共行情请求不走签名，签名客户端按需单独创建
    internal HttpClient CreateAuthenticatedHttpClient()
    {
        if (_credentials is null)
            throw new InvalidOperationException("Gate authenticated endpoints require credentials.");

        return new HttpClient(new GateAuthHandler(_credentials, _timeSync))
        {
            BaseAddress = new Uri(_baseUrl + "/"),
        };
    }

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

    public ValueTask DisposeAsync() => _wsClient.DisposeAsync();

    private static SpotSymbol RequireSpot(Symbol symbol) =>
        symbol as SpotSymbol
        ?? throw new NotSupportedException($"Gate spot market data does not support symbol type {symbol.GetType().Name}.");

    private static Instrument ToInstrument(GateCurrencyPair pair) =>
        new(
            GateSymbolFormatter.ParseSpot(pair.Id),
            TickSize: Pow10Negative(pair.Precision),
            StepSize: Pow10Negative(pair.AmountPrecision),
            MinQuantity: pair.MinBaseAmount is null
                ? 0m
                : decimal.Parse(pair.MinBaseAmount, CultureInfo.InvariantCulture),
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
