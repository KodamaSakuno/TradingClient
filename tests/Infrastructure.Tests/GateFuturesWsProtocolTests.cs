using System.Text.Json;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Gate.WebSocket;

namespace TradingClient.Infrastructure.Tests;

public class GateFuturesWsProtocolTests
{
    private static readonly PerpetualFuturesSymbol BtcUsdtPerp = new("BTC", "USDT");

    // BTC_USDT 乘数 0.0001（1 张 = 0.0001 BTC），与 contracts fixture 一致
    private static decimal GetMultiplier(string contract) =>
        contract == "BTC_USDT"
            ? 0.0001m
            : throw new NotSupportedException($"Unknown contract '{contract}'.");

    // 实帧样本：2026-08-27 录自 testnet futures.book_ticker 推送
    // （实盘探测证实 futures.tickers 推送不含 highest_bid/lowest_ask，最优价改走 book_ticker 频道）
    private const string BookTickerUpdateJson = """
        {
          "time": 1787789060,
          "time_ms": 1787789060341,
          "channel": "futures.book_ticker",
          "event": "update",
          "result": {
            "t": 1787789060341,
            "u": 82069777678,
            "s": "BTC_USDT",
            "b": "79027",
            "B": 53105,
            "a": "79364.9",
            "A": 1
          }
        }
        """;

    // 出处同上 trades notification 示例（不带 X-Gate-Size-Decimal 头的整数形态）
    private const string TradeUpdateJson = """
        {
          "channel": "futures.trades",
          "event": "update",
          "time": 1541503698,
          "time_ms": 1541503698123,
          "result": [
            {
              "size": -108,
              "id": 27753479,
              "create_time": 1545136464,
              "create_time_ms": 1545136464123,
              "price": "96.4",
              "contract": "BTC_USDT"
            }
          ]
        }
        """;

    // 出处同上 order book update notification 示例（整数形态）
    private const string OrderBookUpdateJson = """
        {
          "time": 1615366381,
          "time_ms": 1615366381123,
          "channel": "futures.order_book_update",
          "event": "update",
          "result": {
            "t": 1615366381417,
            "s": "BTC_USDT",
            "U": 2517661101,
            "u": 2517661113,
            "b": [
              { "p": "54672.1", "s": 0 },
              { "p": "54664.5", "s": 58794 }
            ],
            "a": [
              { "p": "54743.6", "s": 0 },
              { "p": "54742", "s": 95 }
            ],
            "l": "100"
          }
        }
        """;

    [Fact]
    public void BuildSubscribeFrame_ForOrderBook_ProducesFuturesPayloadWithIntervalAndLevel()
    {
        var frame = GateWsProtocol.BuildRequestFrame(
            GateFuturesWsProtocol.ChannelOrderBookUpdate, GateWsProtocol.EventSubscribe,
            ["BTC_USDT", GateFuturesWsProtocol.OrderBookInterval, GateFuturesWsProtocol.OrderBookLevel]);

        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Number, root.GetProperty("time").ValueKind);
        Assert.Equal("futures.order_book_update", root.GetProperty("channel").GetString());
        Assert.Equal("subscribe", root.GetProperty("event").GetString());
        Assert.Equal(["BTC_USDT", "100ms", "100"],
            root.GetProperty("payload").EnumerateArray().Select(e => e.GetString()).ToArray());
        // 公共频道无 auth
        Assert.False(root.TryGetProperty("auth", out _));
    }

    [Fact]
    public void BuildPingFrame_Always_ProducesFuturesPingChannel()
    {
        var frame = GateFuturesWsProtocol.BuildPingFrame();

        using var doc = JsonDocument.Parse(frame);
        Assert.Equal("futures.ping", doc.RootElement.GetProperty("channel").GetString());
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("time").ValueKind);
    }

    [Fact]
    public void ToQuote_WithBookTickerUpdate_MapsBestBidAskAndTimestamp()
    {
        var envelope = GateWsProtocol.ParseEnvelope(BookTickerUpdateJson)!;

        var quote = GateFuturesWsProtocol.ToQuote(envelope);

        Assert.NotNull(quote);
        Assert.Equal(BtcUsdtPerp, quote.Symbol);
        Assert.Equal(79027m, quote.BestBid);
        Assert.Equal(79364.9m, quote.BestAsk);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1787789060341), quote.Timestamp);
    }

    [Fact]
    public void ToQuote_WithEmptyAsk_SkipsFrame()
    {
        // 无卖挂单时 a 为空字符串（文档 best ask/bid notification）
        var json = BookTickerUpdateJson.Replace("\"a\": \"79364.9\"", "\"a\": \"\"");
        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        Assert.Null(GateFuturesWsProtocol.ToQuote(envelope));
    }

    [Fact]
    public void ToQuote_WithMissingBid_SkipsFrame()
    {
        var json = BookTickerUpdateJson.Replace("""
            "b": "79027",
            """, "");
        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        Assert.Null(GateFuturesWsProtocol.ToQuote(envelope));
    }

    [Fact]
    public void ToTrades_WithNegativeSize_MapsSellAndContractsToCoins()
    {
        var envelope = GateWsProtocol.ParseEnvelope(TradeUpdateJson)!;

        var trades = GateFuturesWsProtocol.ToTrades(envelope, GetMultiplier);

        var trade = Assert.Single(trades!);
        Assert.Equal("27753479", trade.TradeId);
        Assert.Equal(BtcUsdtPerp, trade.Symbol);
        Assert.Equal(96.4m, trade.Price);
        // 负 size = 主动卖；|108 张| × 0.0001 = 0.0108 币（§7：领域类型不出现张）
        Assert.Equal(OrderSide.Sell, trade.Side);
        Assert.Equal(0.0108m, trade.Quantity);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1545136464123), trade.Timestamp);
    }

    [Fact]
    public void ToTrades_WithPositiveSize_MapsBuy()
    {
        var json = TradeUpdateJson.Replace("\"size\": -108", "\"size\": \"108.5\"");
        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        var trade = Assert.Single(GateFuturesWsProtocol.ToTrades(envelope, GetMultiplier)!);

        Assert.Equal(OrderSide.Buy, trade.Side);
        Assert.Equal(0.01085m, trade.Quantity);
    }

    [Fact]
    public void ToTrades_WithUnknownContract_ThrowsNotSupported()
    {
        var json = TradeUpdateJson.Replace("BTC_USDT", "UNKNOWN_USDT");
        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        Assert.Throws<NotSupportedException>(() => GateFuturesWsProtocol.ToTrades(envelope, GetMultiplier));
    }

    [Fact]
    public void ToOrderBookDelta_WithFullFlag_MapsSnapshotAndContractsToCoins()
    {
        var json = OrderBookUpdateJson.Replace("\"U\":", "\"full\": true, \"U\":");
        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        var delta = GateFuturesWsProtocol.ToOrderBookDelta(envelope, GetMultiplier);

        Assert.NotNull(delta);
        Assert.Equal(BtcUsdtPerp, delta.Symbol);
        Assert.True(delta.IsSnapshot);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1615366381417), delta.Timestamp);
        // 档位量 58794 张 × 0.0001 = 5.8794 币
        Assert.Equal(
            [new OrderBookLevel(54672.1m, 0m), new OrderBookLevel(54664.5m, 5.8794m)],
            delta.Bids);
        Assert.Equal(
            [new OrderBookLevel(54743.6m, 0m), new OrderBookLevel(54742m, 0.0095m)],
            delta.Asks);
    }

    [Fact]
    public void ToOrderBookDelta_WithoutFullField_IsIncremental()
    {
        var envelope = GateWsProtocol.ParseEnvelope(OrderBookUpdateJson)!;

        var delta = GateFuturesWsProtocol.ToOrderBookDelta(envelope, GetMultiplier);

        Assert.NotNull(delta);
        Assert.False(delta.IsSnapshot);
    }

    [Fact]
    public void ToOrderBookDelta_WithZeroSizeLevel_PreservesItForDeletion()
    {
        var envelope = GateWsProtocol.ParseEnvelope(OrderBookUpdateJson)!;

        var delta = GateFuturesWsProtocol.ToOrderBookDelta(envelope, GetMultiplier);

        // s=0 的档位必须原样透传，表示删除该价位
        Assert.Contains(delta!.Bids, l => l.Price == 54672.1m && l.Quantity == 0m);
    }

    [Fact]
    public void ToOrderBookDelta_WithUnknownContract_ThrowsNotSupported()
    {
        var json = OrderBookUpdateJson.Replace("\"s\": \"BTC_USDT\"", "\"s\": \"UNKNOWN_USDT\"");
        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        Assert.Throws<NotSupportedException>(() => GateFuturesWsProtocol.ToOrderBookDelta(envelope, GetMultiplier));
    }

    [Fact]
    public void ExtractSymbol_WithArrayResult_TakesFirstEntryContract()
    {
        var envelope = GateWsProtocol.ParseEnvelope(TradeUpdateJson)!;

        Assert.Equal("BTC_USDT", GateFuturesWsProtocol.ExtractSymbol(envelope));
    }

    [Fact]
    public void ExtractSymbol_WithOrderBookUpdate_TakesSField()
    {
        var envelope = GateWsProtocol.ParseEnvelope(OrderBookUpdateJson)!;

        Assert.Equal("BTC_USDT", GateFuturesWsProtocol.ExtractSymbol(envelope));
    }

    [Fact]
    public void IsUpgradeNotice_WithFuturesSystemUpgradeMessage_ReturnsTrue()
    {
        const string json = """
            {
              "time": 1784800711,
              "time_ms": 1784800711140,
              "channel": "futures.system",
              "event": "update",
              "result": { "type": "upgrade", "msg": "reconnect please" }
            }
            """;

        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        Assert.True(GateFuturesWsProtocol.IsUpgradeNotice(envelope));
    }

    // 出处：.local/gate_api_futures_p_ws.md futures.positions 通知示例（2026-08-27 取，字段裁剪为映射所需）；
    // 推送里数值字段是 JSON number（非字符串），且推送无 unrealised_pnl
    private const string PositionUpdateJson = """
        {
          "time": 1588212926,
          "time_ms": 1588212926123,
          "channel": "futures.positions",
          "event": "update",
          "result": [
            {
              "contract": "BTC_USDT",
              "size": 3,
              "entry_price": 40000.5,
              "history_pnl": -1.5,
              "margin": 49.999,
              "maintenance_rate": 0.005,
              "leverage": 0,
              "cross_leverage_limit": 10,
              "mode": "single",
              "pos_margin_mode": "cross",
              "time_ms": 1628736848321,
              "user": "110xxxxx",
              "update_id": 170919
            }
          ]
        }
        """;

    [Fact]
    public void ToPositionUpdates_SingleModePositiveSize_MapsLong()
    {
        var envelope = GateWsProtocol.ParseEnvelope(PositionUpdateJson)!;

        var update = Assert.Single(GateFuturesWsProtocol.ToPositionUpdates(envelope, GetMultiplier)!);

        Assert.Equal(BtcUsdtPerp, update.Position.Symbol);
        Assert.Equal(PositionSide.Long, update.Position.Side);
        // 3 张 × 0.0001 = 0.0003 币（§7：领域类型不出现张）
        Assert.Equal(0.0003m, update.Position.Quantity);
        Assert.Equal(40000.5m, update.Position.EntryPrice);
        // 推送无 unrealised_pnl，置 0
        Assert.Equal(0m, update.Position.UnrealizedPnl);
        // history_pnl → RealizedPnl（生命周期累计已实现盈亏，§6.4 监控基线差分用）
        Assert.Equal(-1.5m, update.Position.RealizedPnl);
        Assert.Equal(MarginMode.Cross, update.Position.MarginMode);
        // 全仓（leverage 0）实际杠杆上限取 cross_leverage_limit
        Assert.Equal(10, update.Position.Leverage);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1628736848321), update.Timestamp);
    }

    [Fact]
    public void ToPositionUpdates_SingleModeNegativeSize_MapsShort()
    {
        var envelope = GateWsProtocol.ParseEnvelope(PositionUpdateJson.Replace("\"size\": 3", "\"size\": -3"))!;

        var update = Assert.Single(GateFuturesWsProtocol.ToPositionUpdates(envelope, GetMultiplier)!);

        Assert.Equal(PositionSide.Short, update.Position.Side);
        Assert.Equal(0.0003m, update.Position.Quantity);
    }

    [Fact]
    public void ToPositionUpdates_SingleModeZeroSize_MapsBoth()
    {
        // size=0（平完仓的推送）无法定方向，用 Both
        var envelope = GateWsProtocol.ParseEnvelope(PositionUpdateJson.Replace("\"size\": 3", "\"size\": 0"))!;

        var update = Assert.Single(GateFuturesWsProtocol.ToPositionUpdates(envelope, GetMultiplier)!);

        Assert.Equal(PositionSide.Both, update.Position.Side);
    }

    [Fact]
    public void ToPositionUpdates_DualModes_MapDeclaredSides()
    {
        // 双向持仓模式方向由 mode 直接给出，不看 size 符号
        var longEnvelope = GateWsProtocol.ParseEnvelope(
            PositionUpdateJson.Replace("\"mode\": \"single\"", "\"mode\": \"dual_long\""))!;
        var shortEnvelope = GateWsProtocol.ParseEnvelope(
            PositionUpdateJson
                .Replace("\"mode\": \"single\"", "\"mode\": \"dual_short\"")
                .Replace("\"size\": 3", "\"size\": -3"))!;

        Assert.Equal(PositionSide.Long,
            Assert.Single(GateFuturesWsProtocol.ToPositionUpdates(longEnvelope, GetMultiplier)!).Position.Side);
        Assert.Equal(PositionSide.Short,
            Assert.Single(GateFuturesWsProtocol.ToPositionUpdates(shortEnvelope, GetMultiplier)!).Position.Side);
    }

    [Fact]
    public void ToPositionUpdates_WithoutPosMarginMode_FallsBackToLeverage()
    {
        var json = PositionUpdateJson.Replace("\"pos_margin_mode\": \"cross\",", "");
        var crossEnvelope = GateWsProtocol.ParseEnvelope(json)!;
        var isolatedEnvelope = GateWsProtocol.ParseEnvelope(json.Replace("\"leverage\": 0,", "\"leverage\": 10,"))!;

        // leverage 0 = 全仓（语义陷阱同 REST），非 0 = 逐仓
        Assert.Equal(MarginMode.Cross,
            Assert.Single(GateFuturesWsProtocol.ToPositionUpdates(crossEnvelope, GetMultiplier)!).Position.MarginMode);
        var isolated = Assert.Single(GateFuturesWsProtocol.ToPositionUpdates(isolatedEnvelope, GetMultiplier)!);
        Assert.Equal(MarginMode.Isolated, isolated.Position.MarginMode);
        Assert.Equal(10, isolated.Position.Leverage);
    }

    [Fact]
    public void ToPositionUpdates_PosMarginMode_TakesPrecedenceOverLeverageFallback()
    {
        var json = PositionUpdateJson.Replace("\"pos_margin_mode\": \"cross\"", "\"pos_margin_mode\": \"isolated\"");
        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        // leverage 0 本应回退全仓，但 pos_margin_mode=isolated 优先
        var update = Assert.Single(GateFuturesWsProtocol.ToPositionUpdates(envelope, GetMultiplier)!);
        Assert.Equal(MarginMode.Isolated, update.Position.MarginMode);
    }

    // ack 形态（result 是对象而非数组）属于坏消息
    private const string PositionNonArrayResultJson = """
        {
          "time": 1588212926,
          "time_ms": 1588212926123,
          "channel": "futures.positions",
          "event": "update",
          "result": { "status": "success" }
        }
        """;

    [Fact]
    public void ToPositionUpdates_WithNonArrayResult_ReturnsNull()
    {
        var envelope = GateWsProtocol.ParseEnvelope(PositionNonArrayResultJson)!;

        Assert.Null(GateFuturesWsProtocol.ToPositionUpdates(envelope, GetMultiplier));
    }

    [Fact]
    public void ToPositionUpdates_WithMalformedElement_ReturnsNull()
    {
        var envelope = GateWsProtocol.ParseEnvelope(
            PositionUpdateJson.Replace("\"size\": 3", "\"size\": \"abc\""))!;

        Assert.Null(GateFuturesWsProtocol.ToPositionUpdates(envelope, GetMultiplier));
    }

    [Fact]
    public void ToPositionUpdates_WithUnknownContract_ThrowsNotSupported()
    {
        var envelope = GateWsProtocol.ParseEnvelope(PositionUpdateJson.Replace("BTC_USDT", "UNKNOWN_USDT"))!;

        Assert.Throws<NotSupportedException>(() => GateFuturesWsProtocol.ToPositionUpdates(envelope, GetMultiplier));
    }

    [Fact]
    public void ToLiquidationWarnings_BelowThreshold_ReturnsEmpty()
    {
        // margin 49.999 ≫ 维持保证金（12.00015 × 0.005 ≈ 0.06），比率远低于阈值
        var envelope = GateWsProtocol.ParseEnvelope(PositionUpdateJson)!;

        Assert.Empty(GateFuturesWsProtocol.ToLiquidationWarnings(envelope, GetMultiplier)!);
    }

    [Fact]
    public void ToLiquidationWarnings_AboveThreshold_EmitsWarningWithEstimatedPriceBelowEntry()
    {
        // notional = 100 张 × 0.0001 × 50000 = 500；margin 3 → 比率 = 500×0.005/3 ≈ 0.833 ≥ 0.8
        var envelope = GateWsProtocol.ParseEnvelope(HighRiskPositionUpdateJson)!;

        var warning = Assert.Single(GateFuturesWsProtocol.ToLiquidationWarnings(envelope, GetMultiplier)!);

        Assert.Equal(BtcUsdtPerp, warning.Symbol);
        Assert.Equal(PositionSide.Long, warning.Side);
        Assert.Equal(2.5m / 3m, warning.MarginRatio);
        // 线性估算：50000 × (1 − 3/500 + 0.005) = 49950（多头强平价低于入场价）
        Assert.Equal(49950m, warning.EstimatedLiquidationPrice);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1628736848321), warning.Timestamp);
    }

    [Fact]
    public void ToLiquidationWarnings_ShortPositionAboveThreshold_EstimatesPriceAboveEntry()
    {
        var envelope = GateWsProtocol.ParseEnvelope(
            HighRiskPositionUpdateJson.Replace("\"size\": 100", "\"size\": -100"))!;

        var warning = Assert.Single(GateFuturesWsProtocol.ToLiquidationWarnings(envelope, GetMultiplier)!);

        Assert.Equal(PositionSide.Short, warning.Side);
        // 空头镜像：50000 × (1 + 3/500 − 0.005) = 50050
        Assert.Equal(50050m, warning.EstimatedLiquidationPrice);
    }

    [Fact]
    public void ToLiquidationWarnings_ZeroMargin_Skips()
    {
        // margin=0（开仓/平仓中间态）无法计算比率
        var envelope = GateWsProtocol.ParseEnvelope(
            HighRiskPositionUpdateJson.Replace("\"margin\": 3,", "\"margin\": 0,"))!;

        Assert.Empty(GateFuturesWsProtocol.ToLiquidationWarnings(envelope, GetMultiplier)!);
    }

    [Fact]
    public void ToLiquidationWarnings_WithNonArrayResult_ReturnsNull()
    {
        var envelope = GateWsProtocol.ParseEnvelope(PositionNonArrayResultJson)!;

        Assert.Null(GateFuturesWsProtocol.ToLiquidationWarnings(envelope, GetMultiplier));
    }

    // 高保证金占比持仓：margin 3 vs notional 500，触发本地预警阈值
    private static readonly string HighRiskPositionUpdateJson = PositionUpdateJson
        .Replace("\"size\": 3", "\"size\": 100")
        .Replace("\"entry_price\": 40000.5", "\"entry_price\": 50000")
        .Replace("\"margin\": 49.999", "\"margin\": 3");
}
