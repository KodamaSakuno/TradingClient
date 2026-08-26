using System.Globalization;
using System.Net.Http.Json;
using TradingClient.Application.Abstractions;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Primitives;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Bitget.Models;
using TradingClient.Exchanges.Common;

namespace TradingClient.Exchanges.Bitget;

public sealed class BitgetConnector : ExchangeConnectorBase, IMarketData
{
    public const string DefaultBaseUrl = "https://api.bitget.com";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public BitgetConnector(HttpClient httpClient, string baseUrl = DefaultBaseUrl)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public override string ExchangeId => "Bitget";

    // 与 Gate Classic 构成对比象限（§11）：统一账户、无需账户内划转
    public override ExchangeCapabilities Capabilities { get; } = new(
        AccountMode.Unified,
        RequiresInternalTransfers: false,
        Products: [ProductKind.Spot]);

    public override Task ConnectAsync(CancellationToken ct)
    {
        // 校时与鉴权链路在后续步骤补齐
        SetConnectionState(ConnectionState.Connected);
        return Task.CompletedTask;
    }

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
