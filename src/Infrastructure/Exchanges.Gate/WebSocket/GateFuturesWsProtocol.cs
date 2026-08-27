using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Gate.Models;

namespace TradingClient.Exchanges.Gate.WebSocket;

/// <summary>
/// Gate 永续合约公共行情 WS 协议（帧格式与现货同构，复用 GateWsProtocol 的信封解析与请求帧构造）
/// 帧样本出处：.local/gate_api_futures_p_ws.md
/// 张→币换算（§7）：推送的 size/档位量单位是张，乘数由调用方注入查询，领域类型里不出现张
/// </summary>
internal static class GateFuturesWsProtocol
{
    // 最优买卖价用 book_ticker：testnet 实测（2026-08-27）futures.tickers 推送不含 highest_bid/lowest_ask
    public const string ChannelBookTicker = "futures.book_ticker";
    public const string ChannelTrades = "futures.trades";
    public const string ChannelOrderBookUpdate = "futures.order_book_update";
    public const string ChannelPing = "futures.ping";
    public const string ChannelPong = "futures.pong";
    public const string ChannelSystem = "futures.system";

    // order_book_update 频率只支持 20ms（20 档）/100ms（100 档）；选 100ms + 100 档，与现货同款取舍
    public const string OrderBookInterval = "100ms";
    public const string OrderBookLevel = "100";

    // 阶段 3 只接公共频道，订阅帧走 GateWsProtocol.BuildRequestFrame（公共频道无 auth）
    public static string BuildPingFrame() =>
        JsonSerializer.Serialize(
            new GateWsPingRequest(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ChannelPing),
            GateJsonContext.Default.GateWsPingRequest);

    // trades 的 result 是数组，取首元素的 contract 路由；book_ticker/order_book_update 是单对象，取 s
    public static string? ExtractSymbol(GateWsEnvelope envelope)
    {
        if (envelope.Channel is ChannelBookTicker or ChannelOrderBookUpdate)
        {
            return envelope.Result.ValueKind == JsonValueKind.Object
                && envelope.Result.TryGetProperty("s", out var contract)
                ? contract.GetString()
                : null;
        }

        if (envelope.Result.ValueKind == JsonValueKind.Array && envelope.Result.GetArrayLength() > 0)
        {
            var first = envelope.Result[0];
            return first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("contract", out var contract)
                ? contract.GetString()
                : null;
        }

        return null;
    }

    /// <summary>futures.system 的升级通知，收到后应尽快重连</summary>
    public static bool IsUpgradeNotice(GateWsEnvelope envelope) =>
        envelope is { Channel: ChannelSystem, Event: GateWsProtocol.EventUpdate, Result.ValueKind: JsonValueKind.Object }
        && envelope.Result.TryGetProperty("type", out var type)
        && type.GetString() == "upgrade";

    public static Quote? ToQuote(GateWsEnvelope envelope)
    {
        if (envelope.Result.ValueKind != JsonValueKind.Object)
            return null;

        GateFuturesBookTicker? bookTicker;
        try
        {
            bookTicker = envelope.Result.Deserialize(GateJsonContext.Default.GateFuturesBookTicker);
        }
        catch (JsonException)
        {
            return null;
        }

        if (bookTicker is null)
            return null;

        // 无买/卖挂单时对应侧是空字符串，无法构造 Quote，跳过该帧
        if (!TryParseDecimal(bookTicker.BidPrice, out var bid) || !TryParseDecimal(bookTicker.AskPrice, out var ask))
            return null;

        // B/A（最优档数量，单位张）不映射：Domain Quote 无数量字段
        return new Quote(
            GateSymbolFormatter.ParseFutures(bookTicker.Contract),
            bid,
            ask,
            DateTimeOffset.FromUnixTimeMilliseconds(bookTicker.UpdateTimeMs));
    }

    public static Trade[]? ToTrades(GateWsEnvelope envelope, Func<string, decimal> getQuantoMultiplier)
    {
        var trades = DeserializeArray(envelope, GateJsonContext.Default.GateFuturesTradeArray);
        if (trades is null)
            return null;

        var result = new List<Trade>(trades.Length);
        foreach (var trade in trades)
        {
            if (trade.Size == 0 || !TryParseDecimal(trade.Price, out var price))
                continue;

            // size 符号即主动方：正=主动买，负=主动卖；领域 Quantity 为币数量 = |张数| × quanto_multiplier（§7）
            // 未知合约乘数查询抛 NotSupportedException，由订阅管线当坏消息整帧跳过
            var quantity = Math.Abs(trade.Size) * getQuantoMultiplier(trade.Contract);
            var timestamp = trade.CreateTimeMs is { } createMs
                ? DateTimeOffset.FromUnixTimeMilliseconds(createMs)
                : DateTimeOffset.FromUnixTimeSeconds(trade.CreateTime ?? envelope.Time);

            result.Add(new Trade(
                trade.Id.ToString(CultureInfo.InvariantCulture),
                GateSymbolFormatter.ParseFutures(trade.Contract),
                price,
                quantity,
                trade.Size > 0 ? OrderSide.Buy : OrderSide.Sell,
                timestamp));
        }

        return result.ToArray();
    }

    public static OrderBookDelta? ToOrderBookDelta(GateWsEnvelope envelope, Func<string, decimal> getQuantoMultiplier)
    {
        if (envelope.Result.ValueKind != JsonValueKind.Object)
            return null;

        GateFuturesOrderBookUpdate? update;
        try
        {
            update = envelope.Result.Deserialize(GateJsonContext.Default.GateFuturesOrderBookUpdate);
        }
        catch (JsonException)
        {
            return null;
        }

        if (update is null)
            return null;

        // 档位量是张，乘 quanto_multiplier 换成币（§7）；未知合约同上抛 NotSupportedException 当坏消息
        var multiplier = getQuantoMultiplier(update.Contract);

        // full=true 即全量快照（替换本地盘口）；U/u 序列号领域模型无字段不外传，乱序重订不实现
        return new OrderBookDelta(
            GateSymbolFormatter.ParseFutures(update.Contract),
            ToLevels(update.Bids, multiplier),
            ToLevels(update.Asks, multiplier),
            IsSnapshot: update.Full == true,
            DateTimeOffset.FromUnixTimeMilliseconds(update.UpdateTimeMs));
    }

    // s=0 的档位换算后仍为 0，原样透传：表示删除该价位，删档动作由上层盘口维护逻辑执行（现货同款）
    private static OrderBookLevel[] ToLevels(GateFuturesOrderBookLevel[]? levels, decimal multiplier) =>
        levels is null
            ? []
            : levels
                .Select(l => new OrderBookLevel(
                    decimal.Parse(l.Price, CultureInfo.InvariantCulture),
                    l.Size * multiplier))
                .ToArray();

    private static T[]? DeserializeArray<T>(GateWsEnvelope envelope, JsonTypeInfo<T[]> typeInfo)
    {
        if (envelope.Result.ValueKind != JsonValueKind.Array)
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
}
