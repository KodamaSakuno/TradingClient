using System.Text.Json;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Gate.WebSocket;

namespace TradingClient.Infrastructure.Tests;

public class GateWsProtocolTests
{
    private const string TickerUpdateJson = """
        {
          "time": 1669107766,
          "time_ms": 1669107766406,
          "channel": "spot.tickers",
          "event": "update",
          "result": {
            "currency_pair": "BTC_USDT",
            "last": "15743.4",
            "lowest_ask": "15744.4",
            "highest_bid": "15743.5",
            "change_percentage": "-1.8254",
            "base_volume": "9110.473081735",
            "quote_volume": "145082083.2535",
            "high_24h": "16280.9",
            "low_24h": "15468.5"
          }
        }
        """;

    private const string TradeUpdateJson = """
        {
          "time": 1606292218,
          "time_ms": 1606292218231,
          "channel": "spot.trades",
          "event": "update",
          "result": {
            "id": 309143071,
            "id_market": 2390902,
            "create_time": 1606292218,
            "create_time_ms": "1606292218213.4578",
            "side": "sell",
            "currency_pair": "GT_USDT",
            "amount": "16.4700000000",
            "price": "0.4705000000",
            "range": "2390902-2390902"
          }
        }
        """;

    private const string OrderBookSnapshotJson = """
        {
          "time": 1606294781,
          "time_ms": 1606294781236,
          "channel": "spot.order_book_update",
          "event": "update",
          "result": {
            "t": 1606294781123,
            "full": true,
            "l": "100",
            "e": "depthUpdate",
            "E": 1606294781,
            "s": "BTC_USDT",
            "U": 48776301,
            "u": 48776306,
            "b": [["19137.74", "0.0001"], ["19088.37", "0"]],
            "a": [["19137.75", "0.6135"]]
          }
        }
        """;

    [Fact]
    public void ToQuote_WithTickerUpdate_MapsBestBidAskAndTimestamp()
    {
        var envelope = GateWsProtocol.ParseEnvelope(TickerUpdateJson)!;

        var quote = GateWsProtocol.ToQuote(envelope);

        Assert.NotNull(quote);
        Assert.Equal(new SpotSymbol("BTC", "USDT"), quote.Symbol);
        Assert.Equal(15743.5m, quote.BestBid);
        Assert.Equal(15744.4m, quote.BestAsk);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1669107766406), quote.Timestamp);
    }

    [Fact]
    public void ToQuote_WithEmptyBestAsk_ReturnsNull()
    {
        var json = TickerUpdateJson.Replace("\"15744.4\"", "\"\"");
        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        Assert.Null(GateWsProtocol.ToQuote(envelope));
    }

    [Fact]
    public void ToTrade_WithSellTrade_MapsAllFields()
    {
        var envelope = GateWsProtocol.ParseEnvelope(TradeUpdateJson)!;

        var trade = GateWsProtocol.ToTrade(envelope);

        Assert.NotNull(trade);
        Assert.Equal("309143071", trade.TradeId);
        Assert.Equal(new SpotSymbol("GT", "USDT"), trade.Symbol);
        Assert.Equal(0.4705m, trade.Price);
        Assert.Equal(16.47m, trade.Quantity);
        Assert.Equal(OrderSide.Sell, trade.Side);
        // create_time_ms 带亚毫秒小数，截断到毫秒
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1606292218213), trade.Timestamp);
    }

    [Fact]
    public void ToTrade_WithoutCreateTimeMs_FallsBackToCreateTimeSeconds()
    {
        var json = TradeUpdateJson.Replace("""
            "create_time_ms": "1606292218213.4578",
            """, "");
        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        var trade = GateWsProtocol.ToTrade(envelope);

        Assert.NotNull(trade);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1606292218), trade.Timestamp);
    }

    [Fact]
    public void ToOrderBookDelta_WithFullFlag_MapsSnapshotAndLevels()
    {
        var envelope = GateWsProtocol.ParseEnvelope(OrderBookSnapshotJson)!;

        var delta = GateWsProtocol.ToOrderBookDelta(envelope);

        Assert.NotNull(delta);
        Assert.Equal(new SpotSymbol("BTC", "USDT"), delta.Symbol);
        Assert.True(delta.IsSnapshot);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1606294781123), delta.Timestamp);
        Assert.Equal(
            [new OrderBookLevel(19137.74m, 0.0001m), new OrderBookLevel(19088.37m, 0m)],
            delta.Bids);
        Assert.Equal([new OrderBookLevel(19137.75m, 0.6135m)], delta.Asks);
    }

    [Fact]
    public void ToOrderBookDelta_WithoutFullField_IsIncremental()
    {
        var json = OrderBookSnapshotJson.Replace("""
            "full": true,
            """, "");
        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        var delta = GateWsProtocol.ToOrderBookDelta(envelope);

        Assert.NotNull(delta);
        Assert.False(delta.IsSnapshot);
    }

    [Fact]
    public void ToOrderBookDelta_WithZeroAmountLevel_PreservesItForDeletion()
    {
        var envelope = GateWsProtocol.ParseEnvelope(OrderBookSnapshotJson)!;

        var delta = GateWsProtocol.ToOrderBookDelta(envelope);

        // 数量为 0 的档位必须原样透传，表示删除该价位
        Assert.Contains(delta!.Bids, l => l.Price == 19088.37m && l.Quantity == 0m);
    }

    [Fact]
    public void BuildRequestFrame_Always_ProducesGateSubscribeFormat()
    {
        var frame = GateWsProtocol.BuildRequestFrame(
            GateWsProtocol.ChannelOrderBookUpdate, GateWsProtocol.EventSubscribe, ["BTC_USDT", "100ms"]);

        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Number, root.GetProperty("time").ValueKind);
        Assert.Equal("spot.order_book_update", root.GetProperty("channel").GetString());
        Assert.Equal("subscribe", root.GetProperty("event").GetString());
        Assert.Equal(["BTC_USDT", "100ms"],
            root.GetProperty("payload").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void BuildPingFrame_Always_ProducesSpotPingChannel()
    {
        var frame = GateWsProtocol.BuildPingFrame();

        using var doc = JsonDocument.Parse(frame);
        Assert.Equal("spot.ping", doc.RootElement.GetProperty("channel").GetString());
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("time").ValueKind);
    }

    [Fact]
    public void ParseEnvelope_WithMalformedJson_ReturnsNull()
    {
        Assert.Null(GateWsProtocol.ParseEnvelope("not json"));
    }

    [Fact]
    public void IsUpgradeNotice_WithSystemUpgradeMessage_ReturnsTrue()
    {
        const string json = """
            {
              "time": 1784800711,
              "time_ms": 1784800711140,
              "channel": "spot.system",
              "event": "update",
              "result": { "type": "upgrade", "msg": "reconnect please" }
            }
            """;

        var envelope = GateWsProtocol.ParseEnvelope(json)!;

        Assert.True(GateWsProtocol.IsUpgradeNotice(envelope));
    }
}
