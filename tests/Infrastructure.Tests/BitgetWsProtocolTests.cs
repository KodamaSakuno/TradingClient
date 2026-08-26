using System.Text.Json;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Bitget.WebSocket;

namespace TradingClient.Infrastructure.Tests;

public class BitgetWsProtocolTests
{
    // 推送帧样本均取自官方文档示例（.local/bitget/uta/websocket/public/，2026-08 快照）
    private const string TickerPushJson = """
        {
          "data": [
            {
              "bid1Price": "99999",
              "lowPrice24h": "98200",
              "ask1Size": "188.312553",
              "volume24h": "37.722858",
              "price24hPcnt": "0.01833",
              "highPrice24h": "100000",
              "turnover24h": "3750302.979626",
              "bid1Size": "186.183209",
              "ask1Price": "100000",
              "openPrice24h": "0",
              "lastPrice": "100000",
              "platformTurnover24h": "677732572.225658"
            }
          ],
          "arg": {
            "instType": "spot",
            "symbol": "BTCUSDT",
            "topic": "ticker"
          },
          "action": "snapshot",
          "ts": 1736371332162
        }
        """;

    private const string PublicTradePushJson = """
        {
          "data": [
            {
              "p": "100000",
              "S": "buy",
              "T": "1736348770627",
              "v": "0.00118",
              "i": "1260903622036942849",
              "L": "1234568787787878787",
              "isRPI": "no"
            }
          ],
          "arg": {
            "instType": "spot",
            "symbol": "BTCUSDT",
            "topic": "publicTrade"
          },
          "action": "snapshot",
          "ts": 1736371104297
        }
        """;

    private const string BooksPushJson = """
        {
          "data": [
            {
              "a": [
                [
                  "99756.7",
                  "23.9774"
                ]
              ],
              "b": [
                [
                  "99756.6",
                  "0.0128"
                ]
              ],
              "pseq":0,
              "seq": 1304314508780744705,
              "maxDepth": "50",
              "ts": "1746698732562"
            }
          ],
          "arg": {
            "instType": "spot",
            "symbol": "BTCUSDT",
            "topic": "books"
          },
          "action": "snapshot",
          "ts": 1746698732563
        }
        """;

    [Fact]
    public void BuildSubscribeFrame_Always_ProducesOpArgsFormat()
    {
        var frame = BitgetWsProtocol.BuildSubscribeFrame(BitgetWsProtocol.TopicTicker, "BTCUSDT");

        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        Assert.Equal("subscribe", root.GetProperty("op").GetString());
        var arg = Assert.Single(root.GetProperty("args").EnumerateArray());
        Assert.Equal("spot", arg.GetProperty("instType").GetString());
        Assert.Equal("ticker", arg.GetProperty("topic").GetString());
        Assert.Equal("BTCUSDT", arg.GetProperty("symbol").GetString());
    }

    [Fact]
    public void BuildUnsubscribeFrame_Always_ProducesUnsubscribeOp()
    {
        var frame = BitgetWsProtocol.BuildUnsubscribeFrame(BitgetWsProtocol.TopicBooks, "ETHUSDT");

        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        Assert.Equal("unsubscribe", root.GetProperty("op").GetString());
        var arg = Assert.Single(root.GetProperty("args").EnumerateArray());
        Assert.Equal("books", arg.GetProperty("topic").GetString());
        Assert.Equal("ETHUSDT", arg.GetProperty("symbol").GetString());
    }

    [Fact]
    public void ParseEnvelope_WithSubscribeAck_ParsesEventAndArg()
    {
        const string json = """
            {
              "event": "subscribe",
              "arg": {
                "instType": "spot",
                "topic": "ticker",
                "symbol": "BTCUSDT"
              },
              "connId": "xxxxxxxxxx"
            }
            """;

        var envelope = BitgetWsProtocol.ParseEnvelope(json);

        Assert.NotNull(envelope);
        Assert.Equal("subscribe", envelope.Event);
        Assert.NotNull(envelope.Arg);
        Assert.Equal("ticker", envelope.Arg.Topic);
        Assert.Equal("BTCUSDT", envelope.Arg.Symbol);
        Assert.False(BitgetWsProtocol.IsErrorAck(envelope));
    }

    [Fact]
    public void ParseEnvelope_WithErrorAck_ParsesCodeAndMsg()
    {
        const string json = """
            {
              "event": "error",
              "arg": {
                "instType": "spot",
                "topic": "ticker",
                "symbol": "BTCUSDT"
              },
              "code": "30001",
              "msg": "topic is required"
            }
            """;

        var envelope = BitgetWsProtocol.ParseEnvelope(json);

        Assert.NotNull(envelope);
        Assert.True(BitgetWsProtocol.IsErrorAck(envelope));
        Assert.Equal("30001", envelope.Code);
        Assert.Equal("topic is required", envelope.Msg);
    }

    [Fact]
    public void ParseEnvelope_WithNumericCode_ParsesAsString()
    {
        // 实测帧（2026-08-26 模拟盘）：服务端发的 code 是数字而非文档示例的字符串
        const string json = """
            {"event":"error","code":30011,"msg":"Invalid ACCESS_KEY","connId":"06f32efffea6dcaf"}
            """;

        var envelope = BitgetWsProtocol.ParseEnvelope(json);

        Assert.NotNull(envelope);
        Assert.True(BitgetWsProtocol.IsErrorAck(envelope));
        Assert.Equal("30011", envelope.Code);
    }

    [Fact]
    public void IsLoginSuccess_WithNumericZeroCode_ReturnsTrue()
    {
        // 文档示例 login 成功为 "code":"0"，按实测数字形态防御
        const string json = """
            {"event":"login","code":0,"msg":""}
            """;

        var envelope = BitgetWsProtocol.ParseEnvelope(json);

        Assert.NotNull(envelope);
        Assert.True(BitgetWsProtocol.IsLoginSuccess(envelope));
    }

    [Fact]
    public void IsPong_WithLiteralPongTextFrame_ReturnsTrue()
    {
        Assert.True(BitgetWsProtocol.IsPong("pong"));
        Assert.False(BitgetWsProtocol.IsPong("ping"));
        Assert.False(BitgetWsProtocol.IsPong(TickerPushJson));
    }

    [Fact]
    public void ParseEnvelope_WithMalformedJson_ReturnsNull()
    {
        Assert.Null(BitgetWsProtocol.ParseEnvelope("not json"));
        // 字面量 pong 不是 JSON，同样解析为 null（由调用方先用 IsPong 拦截）
        Assert.Null(BitgetWsProtocol.ParseEnvelope("pong"));
    }

    [Fact]
    public void ToQuote_WithTickerPush_MapsBestBidAskAndTimestamp()
    {
        var envelope = BitgetWsProtocol.ParseEnvelope(TickerPushJson)!;

        var quote = BitgetWsProtocol.ToQuote(envelope);

        Assert.NotNull(quote);
        Assert.Equal(new SpotSymbol("BTC", "USDT"), quote.Symbol);
        Assert.Equal(99999m, quote.BestBid);
        Assert.Equal(100000m, quote.BestAsk);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1736371332162), quote.Timestamp);
    }

    [Fact]
    public void ToQuote_WithEmptyBestAsk_ReturnsNull()
    {
        var json = TickerPushJson.Replace("\"100000\"", "\"\"");
        var envelope = BitgetWsProtocol.ParseEnvelope(json)!;

        Assert.Null(BitgetWsProtocol.ToQuote(envelope));
    }

    [Fact]
    public void ToTrades_WithPublicTradePush_MapsSingleLetterFields()
    {
        var envelope = BitgetWsProtocol.ParseEnvelope(PublicTradePushJson)!;

        var trades = BitgetWsProtocol.ToTrades(envelope);

        var trade = Assert.Single(trades!);
        Assert.Equal("1260903622036942849", trade.TradeId);
        Assert.Equal(new SpotSymbol("BTC", "USDT"), trade.Symbol);
        Assert.Equal(100000m, trade.Price);
        Assert.Equal(0.00118m, trade.Quantity);
        Assert.Equal(OrderSide.Buy, trade.Side);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1736348770627), trade.Timestamp);
    }

    [Fact]
    public void ToTrades_WithSellSide_MapsSell()
    {
        var json = PublicTradePushJson.Replace("\"S\": \"buy\"", "\"S\": \"sell\"");
        var envelope = BitgetWsProtocol.ParseEnvelope(json)!;

        var trade = Assert.Single(BitgetWsProtocol.ToTrades(envelope)!);

        Assert.Equal(OrderSide.Sell, trade.Side);
    }

    [Fact]
    public void ToOrderBookDelta_WithSnapshotAction_MapsSnapshotAndLevels()
    {
        var envelope = BitgetWsProtocol.ParseEnvelope(BooksPushJson)!;

        var delta = BitgetWsProtocol.ToOrderBookDelta(envelope);

        Assert.NotNull(delta);
        Assert.Equal(new SpotSymbol("BTC", "USDT"), delta.Symbol);
        Assert.True(delta.IsSnapshot);
        // 时间戳取 data 项的撮合时间戳 ts（字符串毫秒）
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1746698732562), delta.Timestamp);
        Assert.Equal([new OrderBookLevel(99756.6m, 0.0128m)], delta.Bids);
        Assert.Equal([new OrderBookLevel(99756.7m, 23.9774m)], delta.Asks);
    }

    [Fact]
    public void ToOrderBookDelta_WithUpdateAction_IsIncremental()
    {
        var json = BooksPushJson.Replace("\"snapshot\"", "\"update\"");
        var envelope = BitgetWsProtocol.ParseEnvelope(json)!;

        var delta = BitgetWsProtocol.ToOrderBookDelta(envelope);

        Assert.NotNull(delta);
        Assert.False(delta.IsSnapshot);
    }

    [Fact]
    public void ToOrderBookDelta_WithZeroSizeLevel_PreservesItForDeletion()
    {
        var json = BooksPushJson.Replace("\"0.0128\"", "\"0\"");
        var envelope = BitgetWsProtocol.ParseEnvelope(json)!;

        var delta = BitgetWsProtocol.ToOrderBookDelta(envelope);

        // 数量为 0 的档位必须原样透传，表示删除该价位
        Assert.Contains(delta!.Bids, l => l.Price == 99756.6m && l.Quantity == 0m);
    }
}
