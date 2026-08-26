using System.Globalization;
using System.Net.Http.Json;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Common;
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

    public GateConnector(HttpClient httpClient, string baseUrl = DefaultBaseUrl)
        : this(httpClient, baseUrl, new Uri(DefaultWsUrl), () => new ClientWebSocketTransport())
    {
    }

    internal GateConnector(
        HttpClient httpClient,
        string baseUrl,
        Uri wsEndpoint,
        Func<IGateWsTransport> wsTransportFactory,
        TimeSpan? wsPingInterval = null)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _wsClient = new GateSpotWsClient(wsEndpoint, wsTransportFactory, SetConnectionState, ReconnectAsync, wsPingInterval);
    }

    public override string ExchangeId => "Gate";

    public override ExchangeCapabilities Capabilities { get; } = new(
        AccountMode.Classic,
        RequiresInternalTransfers: true,
        Products: [ProductKind.Spot]);

    public override Task ConnectAsync(CancellationToken ct)
    {
        SetConnectionState(ConnectionState.Connected);
        return Task.CompletedTask;
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
