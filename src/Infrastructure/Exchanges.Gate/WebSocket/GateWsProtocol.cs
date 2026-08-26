using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Gate.Auth;
using TradingClient.Exchanges.Gate.Models;

namespace TradingClient.Exchanges.Gate.WebSocket;

/// <summary>
/// Gate 现货 WS 协议（JSON 模式）的帧构造、消息解析与 Domain 映射，纯函数便于单测
/// </summary>
internal static class GateWsProtocol
{
    public const string ChannelTickers = "spot.tickers";
    public const string ChannelTrades = "spot.trades";
    public const string ChannelOrderBookUpdate = "spot.order_book_update";
    public const string ChannelOrders = "spot.orders";
    public const string ChannelPing = "spot.ping";
    public const string ChannelPong = "spot.pong";
    public const string ChannelSystem = "spot.system";

    public const string EventSubscribe = "subscribe";
    public const string EventUnsubscribe = "unsubscribe";
    public const string EventUpdate = "update";

    // spot.orders 支持 "!all" 订阅全部交易对；SpotOrderUpdates 无参拿不到交易对，故固定用它
    public const string OrdersAllPairs = "!all";

    // order_book_update 只支持 20ms（20 档）/100ms（100 档）两种推送间隔
    // （2024-11 changelog 移除了 1000ms）；选 100ms 换取全量快照时的 100 档深度
    public const string OrderBookInterval = "100ms";

    public static string BuildRequestFrame(string channel, string evt, IReadOnlyList<string> payload) =>
        JsonSerializer.Serialize(
            new GateWsRequest(NowSeconds(), channel, evt, payload),
            GateJsonContext.Default.GateWsRequest);

    /// <summary>该频道的 subscribe/unsubscribe 请求体必须带 auth（api_key 签名）</summary>
    public static bool IsPrivateChannel(string channel) => channel == ChannelOrders;

    // 帧的 time 必须与签名串中的 time 一致，由调用方用校时后的时钟统一给出
    public static string BuildAuthenticatedRequestFrame(
        string channel, string evt, IReadOnlyList<string> payload, GateCredentials credentials, long unixTimestamp)
    {
        var auth = new GateWsAuth(
            "api_key", credentials.ApiKey,
            GateSigner.SignWs(credentials.ApiSecret, channel, evt, unixTimestamp));

        return JsonSerializer.Serialize(
            new GateWsAuthenticatedRequest(unixTimestamp, channel, evt, payload, auth),
            GateJsonContext.Default.GateWsAuthenticatedRequest);
    }

    public static string BuildPingFrame() =>
        JsonSerializer.Serialize(
            new GateWsPingRequest(NowSeconds(), ChannelPing),
            GateJsonContext.Default.GateWsPingRequest);

    public static GateWsEnvelope? ParseEnvelope(string json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(json, GateJsonContext.Default.GateWsEnvelope);
            return envelope?.Channel is null or "" ? null : envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? ExtractSymbol(GateWsEnvelope envelope)
    {
        if (envelope.Result.ValueKind != JsonValueKind.Object)
            return null;

        var property = envelope.Channel == ChannelOrderBookUpdate ? "s" : "currency_pair";
        return envelope.Result.TryGetProperty(property, out var value) ? value.GetString() : null;
    }

    /// <summary>spot.system 的升级通知，收到后应尽快重连</summary>
    public static bool IsUpgradeNotice(GateWsEnvelope envelope) =>
        envelope is { Channel: ChannelSystem, Event: EventUpdate, Result.ValueKind: JsonValueKind.Object }
        && envelope.Result.TryGetProperty("type", out var type)
        && type.GetString() == "upgrade";

    public static Quote? ToQuote(GateWsEnvelope envelope)
    {
        var ticker = Deserialize(envelope, GateJsonContext.Default.GateTicker);
        if (ticker is null)
            return null;

        // 无挂单时 Gate 返回空字符串，Quote 无法构造，跳过该帧
        if (!TryParseDecimal(ticker.HighestBid, out var bid) || !TryParseDecimal(ticker.LowestAsk, out var ask))
            return null;

        // ticker 的 result 不带时间戳，用信封的毫秒级响应时间
        var timestamp = envelope.TimeMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.FromUnixTimeSeconds(envelope.Time);

        return new Quote(GateSymbolFormatter.ParseSpot(ticker.CurrencyPair), bid, ask, timestamp);
    }

    public static Trade? ToTrade(GateWsEnvelope envelope)
    {
        var trade = Deserialize(envelope, GateJsonContext.Default.GateTrade);
        if (trade is null)
            return null;

        if (!TryParseDecimal(trade.Price, out var price) || !TryParseDecimal(trade.Amount, out var amount))
            return null;

        var side = trade.Side switch
        {
            "buy" => OrderSide.Buy,
            "sell" => OrderSide.Sell,
            _ => (OrderSide?)null,
        };
        if (side is null)
            return null;

        // create_time_ms 可能带亚毫秒小数（"1606292218213.4578"），DateTimeOffset 只到毫秒，截断处理
        var timestamp = TryParseDecimal(trade.CreateTimeMs, out var msDecimal)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)decimal.Truncate(msDecimal))
            : DateTimeOffset.FromUnixTimeSeconds(trade.CreateTime ?? envelope.Time);

        return new Trade(
            trade.Id.ToString(CultureInfo.InvariantCulture),
            GateSymbolFormatter.ParseSpot(trade.CurrencyPair),
            price,
            amount,
            side.Value,
            timestamp);
    }

    public static OrderBookDelta? ToOrderBookDelta(GateWsEnvelope envelope)
    {
        var update = Deserialize(envelope, GateJsonContext.Default.GateOrderBookUpdate);
        if (update is null)
            return null;

        // 快照语义以正文 full 字段为准：true=全量快照（替换本地盘口），false 时字段不出现
        // changelog 提到的 testnet 快照提前于订阅 ack 推送是 spot.obu（V2 频道）的行为，与本频道无关
        return new OrderBookDelta(
            GateSymbolFormatter.ParseSpot(update.CurrencyPair),
            ToLevels(update.Bids),
            ToLevels(update.Asks),
            IsSnapshot: update.Full == true,
            DateTimeOffset.FromUnixTimeMilliseconds(update.UpdateTimeMs));
    }

    // spot.orders 通知的 result 是订单对象数组，一条通知可含多个交易对的订单（与 tickers 单对象不同）
    public static SpotOrderUpdate[]? ToSpotOrderUpdates(GateWsEnvelope envelope)
    {
        if (envelope.Result.ValueKind != JsonValueKind.Array)
            return null;

        GateSpotOrderUpdate[]? orders;
        try
        {
            orders = envelope.Result.Deserialize(GateJsonContext.Default.GateSpotOrderUpdateArray);
        }
        catch (JsonException)
        {
            return null;
        }

        if (orders is null)
            return null;

        var envelopeTimestamp = envelope.TimeMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.FromUnixTimeSeconds(envelope.Time);

        return orders
            .Select(o => new SpotOrderUpdate(ToSpotOrder(o, envelopeTimestamp), envelopeTimestamp))
            .ToArray();
    }

    // 字段映射与 GateConnector 的 REST 映射对齐，但状态由 event/finish_as 表达（WS 无 status 字段）
    private static SpotOrder ToSpotOrder(GateSpotOrderUpdate dto, DateTimeOffset fallbackTimestamp)
    {
        var quantity = decimal.Parse(dto.Amount, CultureInfo.InvariantCulture);
        var left = decimal.Parse(dto.Left, CultureInfo.InvariantCulture);
        var filled = quantity - left;
        // 统一账户衍生类型以 market_/limit_ 前缀区分（market_borrow 等）
        var type = dto.Type.StartsWith("market", StringComparison.Ordinal) ? OrderType.Market : OrderType.Limit;

        var status = dto.Event switch
        {
            "put" => OrderStatus.New,
            "update" => filled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.New,
            // cancelled/ioc/stp/poc/fok 等一律归入 Cancelled（部分成交量体现在 FilledQuantity）
            "finish" => dto.FinishAs == "filled" ? OrderStatus.Filled : OrderStatus.Cancelled,
            // 未知事件视为协议漂移（坏消息）：Subscribe 管线捕获异常后跳过该帧
            _ => throw new NotSupportedException($"Unknown Gate spot order event '{dto.Event}'."),
        };

        var createdAt = long.TryParse(dto.CreateTimeMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var createMs)
            ? DateTimeOffset.FromUnixTimeMilliseconds(createMs)
            : fallbackTimestamp;

        return new SpotOrder(
            dto.Id,
            GateSymbolFormatter.ParseSpot(dto.CurrencyPair),
            dto.Side == "buy" ? OrderSide.Buy : OrderSide.Sell,
            type,
            // market 单领域语义 Price=null
            type == OrderType.Market || dto.Price is null
                ? null
                : decimal.Parse(dto.Price, CultureInfo.InvariantCulture),
            quantity,
            filled,
            status,
            createdAt);
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

    private static T? Deserialize<T>(GateWsEnvelope envelope, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (envelope.Result.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            return envelope.Result.Deserialize(typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseDecimal(string? value, out decimal result)
    {
        result = 0;
        return !string.IsNullOrEmpty(value)
            && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    private static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
