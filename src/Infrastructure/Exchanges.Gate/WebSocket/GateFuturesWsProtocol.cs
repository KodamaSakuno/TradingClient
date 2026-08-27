using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Gate.Models;

namespace TradingClient.Exchanges.Gate.WebSocket;

/// <summary>
/// Gate 永续合约 WS 协议（帧格式与现货同构，复用 GateWsProtocol 的信封解析与请求帧构造）
/// 帧样本出处：.local/gate_api_futures_p_ws.md
/// 张→币换算：推送的 size/档位量单位是张，乘数由调用方注入查询，领域类型里不出现张
/// </summary>
internal static class GateFuturesWsProtocol
{
    // 最优买卖价用 book_ticker：testnet 实测（2026-08-27）futures.tickers 推送不含 highest_bid/lowest_ask
    public const string ChannelBookTicker = "futures.book_ticker";
    public const string ChannelTrades = "futures.trades";
    public const string ChannelOrderBookUpdate = "futures.order_book_update";
    // 私有持仓频道：subscribe/unsubscribe 请求体必须带 auth（GateWsProtocol.IsPrivateChannel）
    public const string ChannelPositions = "futures.positions";
    public const string ChannelPing = "futures.ping";
    public const string ChannelPong = "futures.pong";
    public const string ChannelSystem = "futures.system";

    // order_book_update 频率只支持 20ms（20 档）/100ms（100 档）；选 100ms + 100 档，与现货同款取舍
    public const string OrderBookInterval = "100ms";
    public const string OrderBookLevel = "100";

    // futures.positions 订阅 payload 首位是 user id，文档注明该字段已废弃、仅作占位符
    public const string PositionsUserPlaceholder = "!";
    // 文档原文："If you want to subscribe to position updates in all contracts, use `!all` in the contract list."
    public const string PositionsAllContracts = "!all";

    // Gate 期货无事前强平预警频道，推送里的 liq_price 已 Deprecated（示例返回 0.1 垃圾值，不可用）；
    // 预警靠本地估算：维持保证金 / 持仓保证金达此阈值即发 LiquidationWarning。
    // 事后强平通知另有 futures.liquidates 频道，本刀不接
    public const decimal LiquidationWarningMarginRatioThreshold = 0.8m;

    // 私有频道订阅帧走 GateWsProtocol.BuildAuthenticatedRequestFrame（GateFuturesWsClient.BuildRequestFrame 分派）
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

            // size 符号即主动方：正=主动买，负=主动卖；领域 Quantity 为币数量 = |张数| × quanto_multiplier
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

        // 档位量是张，乘 quanto_multiplier 换成币；未知合约同上抛 NotSupportedException 当坏消息
        var multiplier = getQuantoMultiplier(update.Contract);

        // full=true 即全量快照（替换本地盘口）；U/u 序列号领域模型无字段不外传，乱序重订不实现
        return new OrderBookDelta(
            GateSymbolFormatter.ParseFutures(update.Contract),
            ToLevels(update.Bids, multiplier),
            ToLevels(update.Asks, multiplier),
            IsSnapshot: update.Full == true,
            DateTimeOffset.FromUnixTimeMilliseconds(update.UpdateTimeMs));
    }

    // futures.positions 通知的 result 是持仓数组（!all 订阅，一条通知可含多个合约），与现货 spot.orders 同款形态
    public static PositionUpdate[]? ToPositionUpdates(GateWsEnvelope envelope, Func<string, decimal> getQuantoMultiplier)
    {
        var dtos = DeserializeArray(envelope, GateJsonContext.Default.GateFuturesPositionUpdateArray);
        if (dtos is null)
            return null;

        var fallbackTimestamp = EnvelopeTimestamp(envelope);
        // 未知合约乘数查询抛 NotSupportedException，由订阅管线当坏消息整帧跳过（同 trades）
        return dtos.Select(dto => new PositionUpdate(ToPosition(dto, getQuantoMultiplier), TimestampOf(dto, fallbackTimestamp)))
            .ToArray();
    }

    // 基于同一条 futures.positions 推送本地估算强平预警（缘由见 LiquidationWarningMarginRatioThreshold 注释）。
    // 需要原始 DTO 的 margin/maintenance_rate（Domain Position 不带），达阈值的持仓每条推送都重发，去重留给 UI
    public static LiquidationWarning[]? ToLiquidationWarnings(GateWsEnvelope envelope, Func<string, decimal> getQuantoMultiplier)
    {
        var dtos = DeserializeArray(envelope, GateJsonContext.Default.GateFuturesPositionUpdateArray);
        if (dtos is null)
            return null;

        var fallbackTimestamp = EnvelopeTimestamp(envelope);
        return dtos
            .Select(dto => ToLiquidationWarning(dto, getQuantoMultiplier, fallbackTimestamp))
            .OfType<LiquidationWarning>()
            .ToArray();
    }

    private static LiquidationWarning? ToLiquidationWarning(
        GateFuturesPositionUpdate dto, Func<string, decimal> getQuantoMultiplier, DateTimeOffset fallbackTimestamp)
    {
        // margin=0（开仓/平仓中间态）或无名义价值的帧无法计算比率，跳过
        if (dto.Margin <= 0)
            return null;

        var notional = Math.Abs(dto.Size) * getQuantoMultiplier(dto.Contract) * dto.EntryPrice;
        if (notional <= 0)
            return null;

        var marginRatio = notional * dto.MaintenanceRate / dto.Margin;
        if (marginRatio < LiquidationWarningMarginRatioThreshold)
            return null;

        var side = ToSide(dto.Mode, dto.Size);
        // 线性估算强平价（仅供预警，非交易所口径）：价格走到保证金只剩维持保证金的位置。
        // 多头 entry × (1 − margin/notional + maintenance_rate)，空头镜像
        var estimatedPrice = side == PositionSide.Short
            ? dto.EntryPrice * (1 + dto.Margin / notional - dto.MaintenanceRate)
            : dto.EntryPrice * (1 - dto.Margin / notional + dto.MaintenanceRate);

        return new LiquidationWarning(
            GateSymbolFormatter.ParseFutures(dto.Contract),
            side,
            estimatedPrice,
            marginRatio,
            TimestampOf(dto, fallbackTimestamp));
    }

    private static Position ToPosition(GateFuturesPositionUpdate dto, Func<string, decimal> getQuantoMultiplier) =>
        new(
            GateSymbolFormatter.ParseFutures(dto.Contract),
            ToSide(dto.Mode, dto.Size),
            Math.Abs(dto.Size) * getQuantoMultiplier(dto.Contract),
            dto.EntryPrice,
            // 推送无 unrealised_pnl 字段（WS 与 REST 字段集不同），Domain 必填，置 0
            dto.UnrealisedPnl ?? 0m,
            // leverage 0=全仓（语义同 REST），全仓实际杠杆上限取 cross_leverage_limit
            dto.Leverage != 0 ? (int)dto.Leverage : (int)dto.CrossLeverageLimit,
            ToMarginMode(dto),
            // history_pnl 是生命周期累计已实现盈亏（无日切字段），供监控做基线差分
            dto.HistoryPnl);

    // single 模式按带符号张数定方向（size=0 无法定方向，用 Both）；dual_* 由 mode 直接给出
    private static PositionSide ToSide(string mode, long size) => mode switch
    {
        "dual_long" => PositionSide.Long,
        "dual_short" => PositionSide.Short,
        _ => size > 0 ? PositionSide.Long : size < 0 ? PositionSide.Short : PositionSide.Both,
    };

    // pos_margin_mode 优先；缺省时回退 leverage==0 → 全仓（leverage 语义陷阱同 REST）
    private static MarginMode ToMarginMode(GateFuturesPositionUpdate dto) => dto.PosMarginMode switch
    {
        "cross" => MarginMode.Cross,
        "isolated" => MarginMode.Isolated,
        _ => dto.Leverage == 0 ? MarginMode.Cross : MarginMode.Isolated,
    };

    private static DateTimeOffset EnvelopeTimestamp(GateWsEnvelope envelope) =>
        envelope.TimeMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.FromUnixTimeSeconds(envelope.Time);

    private static DateTimeOffset TimestampOf(GateFuturesPositionUpdate dto, DateTimeOffset fallbackTimestamp) =>
        dto.TimeMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : fallbackTimestamp;

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
