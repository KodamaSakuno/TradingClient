using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Bitget.Auth;
using TradingClient.Exchanges.Bitget.Models;

namespace TradingClient.Exchanges.Bitget.WebSocket;

/// <summary>
/// Bitget UTA WS 帧构造、消息解析与 Domain 映射，纯函数便于单测；
/// 覆盖公共行情频道与私有 order 频道（.local/bitget/uta/websocket/）
/// </summary>
internal static class BitgetWsProtocol
{
    public const string InstTypeSpot = "spot";
    // 私有频道 instType 是大写 UTA、账户级不带 symbol，与公共频道的小写 spot 不同
    public const string InstTypeUta = "UTA";

    public const string TopicTicker = "ticker";
    public const string TopicPublicTrade = "publicTrade";
    // books 为全量深度频道（首帧 snapshot 后续 update）；books1/5/50 档位频道恒为 snapshot，不用
    public const string TopicBooks = "books";
    public const string TopicOrder = "order";

    // order 频道推送的 data 项用 category 区分产品线，现货为 "spot"
    public const string CategorySpot = "spot";

    public const string OpSubscribe = "subscribe";
    public const string OpUnsubscribe = "unsubscribe";
    public const string OpLogin = "login";

    public const string EventError = "error";
    public const string EventLogin = "login";
    public const string LoginSuccessCode = "0";
    public const string ActionSnapshot = "snapshot";

    // 心跳为字面量文本帧而非 JSON：每 30 秒发 "ping"，服务端回 "pong"；2 分钟无 ping 服务端断连
    public const string PingText = "ping";
    public const string PongText = "pong";

    public static string BuildSubscribeFrame(string topic, string symbol) =>
        BuildFrame(OpSubscribe, topic, symbol);

    public static string BuildUnsubscribeFrame(string topic, string symbol) =>
        BuildFrame(OpUnsubscribe, topic, symbol);

    public static string BuildFrame(string op, string topic, string symbol) =>
        JsonSerializer.Serialize(
            new BitgetWsRequest(op, [new BitgetWsChannelArg(InstTypeSpot, topic, symbol)]),
            BitgetJsonContext.Default.BitgetWsRequest);

    // 私有 order 频道订阅帧：{"op":"subscribe","args":[{"instType":"UTA","topic":"order"}]}
    public static string BuildOrderChannelFrame(string op) =>
        JsonSerializer.Serialize(
            new BitgetWsRequest(op, [new BitgetWsChannelArg(InstTypeUta, TopicOrder, null)]),
            BitgetJsonContext.Default.BitgetWsRequest);

    // WS 登录的 timestamp 是秒级 Unix 时间（与 REST 签名的毫秒不同，quick-start 示例 "1538054050"）；
    // 签名串固定为 timestamp + "GET" + "/user/verify"
    public static string BuildLoginFrame(BitgetCredentials credentials, long unixTimestampSeconds)
    {
        var timestamp = unixTimestampSeconds.ToString(CultureInfo.InvariantCulture);
        var sign = BitgetSigner.Sign(credentials.ApiSecret, timestamp, "GET", "/user/verify", null, null);
        return JsonSerializer.Serialize(
            new BitgetWsLoginRequest(OpLogin, [new BitgetWsLoginArg(
                credentials.ApiKey, credentials.Passphrase, timestamp, sign)]),
            BitgetJsonContext.Default.BitgetWsLoginRequest);
    }

    public static bool IsPong(string message) => message.Trim() == PongText;

    /// <summary>解析入站帧；非 JSON（含字面量 pong）或既非 ack 也非推送的帧返回 null</summary>
    public static BitgetWsEnvelope? ParseEnvelope(string json)
    {
        BitgetWsEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(json, BitgetJsonContext.Default.BitgetWsEnvelope);
        }
        catch (JsonException)
        {
            return null;
        }

        // ack 带 event，推送带 arg+action；两者都无的帧无法路由
        if (envelope?.Event is not null || envelope?.Arg is not null)
            return envelope;

        return null;
    }

    public static bool IsErrorAck(BitgetWsEnvelope envelope) => envelope.Event == EventError;

    public static bool IsLoginSuccess(BitgetWsEnvelope envelope) =>
        envelope.Event == EventLogin && envelope.Code == LoginSuccessCode;

    public static Quote? ToQuote(BitgetWsEnvelope envelope)
    {
        var tickers = DeserializeData(envelope, BitgetJsonContext.Default.BitgetWsTickerArray);
        var ticker = tickers is { Length: > 0 } ? tickers[0] : null;
        if (ticker is null)
            return null;

        // 无挂单时字段可能为空字符串，Quote 无法构造，跳过该帧
        if (!TryParseDecimal(ticker.Bid1Price, out var bid) || !TryParseDecimal(ticker.Ask1Price, out var ask))
            return null;

        // Domain Quote 无最新价字段（lastPrice 不外传）；时间戳取信封 ts（毫秒）
        return new Quote(
            BitgetSymbolFormatter.ParseSpot(envelope.Arg!.Symbol!),
            bid,
            ask,
            TimestampFromMs(envelope.Ts));
    }

    // 一条推送的 data 可含多笔成交，映射后由调用方展开
    public static Trade[]? ToTrades(BitgetWsEnvelope envelope)
    {
        var trades = DeserializeData(envelope, BitgetJsonContext.Default.BitgetWsPublicTradeArray);
        if (trades is null)
            return null;

        var symbol = BitgetSymbolFormatter.ParseSpot(envelope.Arg!.Symbol!);
        var result = new List<Trade>(trades.Length);
        foreach (var trade in trades)
        {
            if (!TryParseDecimal(trade.Price, out var price) || !TryParseDecimal(trade.Quantity, out var quantity))
                continue;

            var side = trade.Side switch
            {
                "buy" => OrderSide.Buy,
                "sell" => OrderSide.Sell,
                _ => (OrderSide?)null,
            };
            if (side is null)
                continue;

            result.Add(new Trade(
                trade.TradeId,
                symbol,
                price,
                quantity,
                side.Value,
                TimestampFromMs(trade.TimestampMs, envelope.Ts)));
        }

        return result.ToArray();
    }

    public static OrderBookDelta? ToOrderBookDelta(BitgetWsEnvelope envelope)
    {
        var books = DeserializeData(envelope, BitgetJsonContext.Default.BitgetWsBookArray);
        var book = books is { Length: > 0 } ? books[0] : null;
        if (book is null)
            return null;

        // 快照语义以信封 action 为准：snapshot=全量替换本地盘口，update=增量
        return new OrderBookDelta(
            BitgetSymbolFormatter.ParseSpot(envelope.Arg!.Symbol!),
            ToLevels(book.Bids),
            ToLevels(book.Asks),
            IsSnapshot: envelope.Action == ActionSnapshot,
            TimestampFromMs(book.MatchTimestampMs, envelope.Ts));
    }

    // UTA order 频道是账户级全产品线推送（无 symbol 维度），data 项以 category 区分产品线；
    // 现货推送流只放行 category=="spot"，合约等推送留待阶段 3 的合约适配消费
    public static SpotOrderUpdate[]? ToSpotOrderUpdates(BitgetWsEnvelope envelope)
    {
        var orders = DeserializeData(envelope, BitgetJsonContext.Default.BitgetWsOrderArray);
        if (orders is null)
            return null;

        var result = new List<SpotOrderUpdate>(orders.Length);
        foreach (var order in orders)
        {
            if (!string.Equals(order.Category, CategorySpot, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new SpotOrderUpdate(
                ToSpotOrder(order, envelope.Ts),
                TimestampFromMs(order.UpdatedTime, envelope.Ts)));
        }

        return result.ToArray();
    }

    private static SpotOrder ToSpotOrder(BitgetWsOrder dto, long? fallbackTimestampMs)
    {
        var side = dto.Side switch
        {
            "buy" => OrderSide.Buy,
            "sell" => OrderSide.Sell,
            // 未知方向视为坏消息：抛出后由 Subscribe 管线捕获并跳过整帧
            _ => throw new NotSupportedException($"Unknown Bitget spot order side '{dto.Side}'."),
        };
        var type = dto.OrderType == "market" ? OrderType.Market : OrderType.Limit;

        var status = dto.OrderStatus switch
        {
            "new" => OrderStatus.New,
            "partially_filled" => OrderStatus.PartiallyFilled,
            "filled" => OrderStatus.Filled,
            "cancelled" => OrderStatus.Cancelled,
            // 未知状态视为协议漂移（坏消息）：Subscribe 管线捕获异常后跳过该帧
            _ => throw new NotSupportedException($"Unknown Bitget spot order status '{dto.OrderStatus}'."),
        };

        return new SpotOrder(
            dto.OrderId ?? string.Empty,
            BitgetSymbolFormatter.ParseSpot(dto.Symbol!),
            side,
            type,
            // market 单领域语义 Price=null；推送中 market 单的 price 为空字符串
            type == OrderType.Market || string.IsNullOrEmpty(dto.Price)
                ? null
                : decimal.Parse(dto.Price, CultureInfo.InvariantCulture),
            decimal.Parse(dto.Qty!, CultureInfo.InvariantCulture),
            decimal.Parse(dto.CumExecQty!, CultureInfo.InvariantCulture),
            status,
            TimestampFromMs(dto.CreatedTime, fallbackTimestampMs));
    }

    // 数量为 0 的档位原样透传：表示删除该价位，删档动作由上层盘口维护逻辑执行
    private static OrderBookLevel[] ToLevels(string[][]? levels) =>
        levels is null
            ? []
            : levels
                .Where(l => l.Length >= 2)
                .Select(l => new OrderBookLevel(
                    decimal.Parse(l[0], CultureInfo.InvariantCulture),
                    decimal.Parse(l[1], CultureInfo.InvariantCulture)))
                .ToArray();

    private static T? DeserializeData<T>(BitgetWsEnvelope envelope, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (envelope.Data.ValueKind != JsonValueKind.Array)
            return null;

        try
        {
            return envelope.Data.Deserialize(typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset TimestampFromMs(string? value, long? fallback) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : TimestampFromMs(fallback);

    private static DateTimeOffset TimestampFromMs(long? ms) =>
        ms is { } value
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.UtcNow;

    private static bool TryParseDecimal(string? value, out decimal result)
    {
        result = 0;
        return !string.IsNullOrEmpty(value)
            && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }
}
