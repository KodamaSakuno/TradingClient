using System.Globalization;
using System.Net.Http.Json;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Common;
using TradingClient.Exchanges.Gate.Models;

namespace TradingClient.Exchanges.Gate;

public sealed class GateConnector(HttpClient httpClient, string baseUrl = GateConnector.DefaultBaseUrl)
    : ExchangeConnectorBase, IMarketData
{
    public const string DefaultBaseUrl = "https://api.gateio.ws";

    private readonly string _baseUrl = baseUrl.TrimEnd('/');

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

        var pairs = await httpClient.GetFromJsonAsync(
            $"{_baseUrl}/api/v4/spot/currency_pairs",
            GateJsonContext.Default.GateCurrencyPairArray, ct);

        return pairs?.Select(ToInstrument).ToArray() ?? [];
    }

    public IObservable<Quote> SubscribeQuotes(Symbol symbol) => throw new NotImplementedException();

    public IObservable<Trade> SubscribeTrades(Symbol symbol) => throw new NotImplementedException();

    public IObservable<OrderBookDelta> SubscribeOrderBook(Symbol symbol) => throw new NotImplementedException();

    public IObservable<Candle> SubscribeCandles(Symbol symbol, TimeFrame tf) => throw new NotImplementedException();

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
