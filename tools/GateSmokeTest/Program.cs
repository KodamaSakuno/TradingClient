using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingClient.Application.Abstractions;
using TradingClient.Application.Services;
using TradingClient.Application.UseCases.Spot;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;

// Gate testnet 现货冒烟测试：连接 → 余额 → 远离市价的限价买单 → 撤单
// 验证真实链路：签名（GateAuthHandler 由 GateConnector 内部接好）、时间同步、限流、用例层编排
// 凭证只走环境变量，永不打印 secret，key 只打前 4 位掩码（§9）

const string TestnetBaseUrl = "https://api-testnet.gateapi.io";

static void Log(string step, string message) =>
    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [{step}] {message}");

// ---------- 1. 凭证 ----------
var apiKey = Environment.GetEnvironmentVariable("GATE_TESTNET_API_KEY");
var apiSecret = Environment.GetEnvironmentVariable("GATE_TESTNET_API_SECRET");
if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
{
    Console.WriteLine("""
        Gate testnet 现货冒烟测试

        用法:
          设置环境变量后运行:
            GATE_TESTNET_API_KEY=<testnet api key>
            GATE_TESTNET_API_SECRET=<testnet api secret>
          dotnet run --project tools/GateSmokeTest -- [CURRENCY_PAIR]

        参数:
          CURRENCY_PAIR   Gate 现货交易对，支持 BTC_USDT 或 BTC/USDT 格式，默认 BTC_USDT

        流程: 连接 testnet -> 查余额 -> 下一笔远离市价的小额限价买单 -> 立即撤单
        """);
    return 2;
}

var maskedKey = apiKey.Length >= 4 ? apiKey[..4] + "****" : "****";
Log("凭证", $"已读取环境变量，ApiKey={maskedKey}");

// ---------- 2. 交易对参数 ----------
var pairArg = args.Length > 0 ? args[0] : "BTC_USDT";
var parts = pairArg.Replace('/', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
if (parts.Length != 2)
{
    Log("参数", $"失败：无法解析交易对 '{pairArg}'，期望格式 BTC_USDT");
    return 1;
}
var symbol = new SpotSymbol(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
var gatePair = $"{symbol.Base}_{symbol.Quote}";
Log("参数", $"目标交易对 {gatePair}");

var failed = false;
using var httpClient = new HttpClient();
await using var connector = new GateConnector(
    httpClient, TestnetBaseUrl, new GateCredentials(apiKey, apiSecret));

// ---------- 3. 连接（内含 /spot/time 校时，失败降级本地时钟） ----------
try
{
    await connector.ConnectAsync(CancellationToken.None);
    Log("连接", $"成功（testnet: {TestnetBaseUrl}）");
}
catch (Exception ex)
{
    Log("连接", $"失败：{ex.Message}");
    return 1;
}

// ---------- 4. 拉取 instruments 建缓存，校验目标交易对可交易 ----------
var instruments = new InstrumentCache(connector);
Instrument? instrument = null;
try
{
    await instruments.RefreshAsync(ProductKind.Spot, CancellationToken.None);
    instrument = await instruments.GetAsync(symbol, CancellationToken.None);
    if (instrument is null)
    {
        Log("Instruments", $"失败：testnet 上找不到 {gatePair}");
        failed = true;
    }
    else if (instrument.Status != InstrumentStatus.Trading)
    {
        Log("Instruments", $"失败：{gatePair} 状态为 {instrument.Status}，不可交易");
        failed = true;
    }
    else
    {
        Log("Instruments", $"OK：TickSize={instrument.TickSize} StepSize={instrument.StepSize} MinQuantity={instrument.MinQuantity}");
    }
}
catch (Exception ex)
{
    Log("Instruments", $"失败：{ex.Message}");
    failed = true;
}

// ---------- 5. 公共 REST ticker 取最新价 ----------
decimal? lastPrice = null;
try
{
    var tickers = await httpClient.GetFromJsonAsync(
        $"{TestnetBaseUrl}/api/v4/spot/tickers?currency_pair={gatePair}",
        SmokeJsonContext.Default.GateTickerArray);
    var last = tickers is { Length: > 0 } ? tickers[0].Last : null;
    if (last is null || !decimal.TryParse(last, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
    {
        Log("Ticker", $"失败：{gatePair} 无有效最新价（last='{last ?? "<null>"}'）");
        failed = true;
    }
    else
    {
        lastPrice = parsed;
        Log("Ticker", $"OK：{gatePair} last={parsed.ToString(CultureInfo.InvariantCulture)}");
    }
}
catch (Exception ex)
{
    Log("Ticker", $"失败：{ex.Message}");
    failed = true;
}

// ---------- 6. 余额 ----------
var account = await connector.GetAccountAsync(CancellationToken.None);
if (!account.IsSuccess)
{
    Log("余额", $"失败：[{account.Error!.Code}] {account.Error.Message}");
    failed = true;
}
else
{
    var assets = account.Value!.Assets;
    Log("余额", $"OK：{assets.Count} 个币种");
    foreach (var a in assets)
        Log("余额", $"  {a.Asset}: Total={a.Total} Frozen={a.Frozen}");
}

// ---------- 7. 下单（走用例层，顺带验证编排与对齐校验） ----------
string? orderId = null;
if (instrument is not null && lastPrice is { } marketPrice)
{
    var price = instrument.AlignPrice(marketPrice * 0.5m);
    // Instrument 暂未携带 min_quote_amount（领域缺口，待补），按目标名义金额反推数量
    const decimal targetNotional = 5m;
    var minQty = instrument.MinQuantity > 0 ? instrument.MinQuantity : instrument.StepSize;
    var notionalQty = instrument.AlignQuantity(targetNotional / price);
    var quantity = notionalQty > minQty ? notionalQty : minQty;

    Log("下单", $"请求：Limit Buy {gatePair} Price={price.ToString(CultureInfo.InvariantCulture)} Quantity={quantity.ToString(CultureInfo.InvariantCulture)}（市价的 50%，保证不成交）");

    var placeOrder = new PlaceSpotOrder(connector, instruments);
    var placed = await placeOrder.ExecuteAsync(
        new PlaceSpotOrderRequest(symbol, OrderSide.Buy, OrderType.Limit, price, quantity),
        CancellationToken.None);

    if (!placed.IsSuccess)
    {
        Log("下单", $"失败：[{placed.Error!.Code}] {placed.Error.Message}");
        failed = true;
    }
    else
    {
        var order = placed.Value!;
        orderId = order.OrderId;
        Log("下单", $"OK：OrderId={order.OrderId} Status={order.Status}");
    }
}
else
{
    Log("下单", "跳过：前置步骤（Instruments/Ticker）失败");
    failed = true;
}

// ---------- 8. 撤单 ----------
if (orderId is not null)
{
    var cancelOrder = new CancelSpotOrder(connector);
    var cancelled = await cancelOrder.ExecuteAsync(symbol, orderId, CancellationToken.None);
    if (!cancelled.IsSuccess)
    {
        Log("撤单", $"失败：[{cancelled.Error!.Code}] {cancelled.Error.Message}");
        failed = true;
    }
    else
    {
        Log("撤单", $"OK：OrderId={orderId}");
    }
}
else
{
    Log("撤单", "跳过：下单未成功");
}

Log("结果", failed ? "冒烟测试未全部通过" : "全链路通过");
return failed ? 1 : 0;

// Gate ticker 公共响应 DTO：定义在本工具内，不动 Exchanges.Gate 的 internal DTO
internal sealed record GateTicker(
    [property: JsonPropertyName("last")] string? Last);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GateTicker[]))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;
