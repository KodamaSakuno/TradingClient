using System.Globalization;
using System.Net;
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

// Gate testnet 现货冒烟测试：连接 → 余额 → 限价买单 → 撤单（默认）或立即成交（--fill）
// --fill 模式：订阅订单私有推送，价格挂在市价上方保证立即成交，打印成交流水并复查余额
// 验证真实链路：签名（GateAuthHandler 由 GateConnector 内部接好）、时间同步、限流、用例层编排
// 凭证只走环境变量，永不打印 secret，key 只打前 4 位掩码（§9）

const string TestnetBaseUrl = "https://api-testnet.gateapi.io";
const string TestnetWsUrl = "wss://ws-testnet.gate.com/v4/ws/spot";

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
          dotnet run --project tools/GateSmokeTest -- [CURRENCY_PAIR] [--fill]

        参数:
          CURRENCY_PAIR   Gate 现货交易对，支持 BTC_USDT 或 BTC/USDT 格式，默认 BTC_USDT
          --fill          成交模式：价格挂在市价上方立即成交，并订阅订单私有推送打印成交流水

        流程: 连接 testnet -> 查余额 -> 限价买单（默认远离市价不成交；--fill 立即成交）-> 撤单（--fill 成交后跳过）
        """);
    return 2;
}

var maskedKey = apiKey.Length >= 4 ? apiKey[..4] + "****" : "****";
Log("凭证", $"已读取环境变量，ApiKey={maskedKey}");

// ---------- 2. 交易对参数与模式 ----------
var fillMode = args.Contains("--fill");
var pairArg = args.Length > 0 && args[0] != "--fill" ? args[0] : "BTC_USDT";
var parts = pairArg.Replace('/', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
if (parts.Length != 2)
{
    Log("参数", $"失败：无法解析交易对 '{pairArg}'，期望格式 BTC_USDT");
    return 1;
}
var symbol = new SpotSymbol(parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
var gatePair = $"{symbol.Base}_{symbol.Quote}";
Log("参数", $"目标交易对 {gatePair}（{(fillMode ? "fill 模式：市价上方挂单立即成交" : "默认模式：半价挂单后撤单")}）");

var failed = false;
// WS 代理（可选）：testnet 的 WS 端点在部分网络环境下需要代理，REST 不通时同理（走自供 httpClient）
var proxyArg = Environment.GetEnvironmentVariable("GATE_TESTNET_PROXY") ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
var wsProxy = string.IsNullOrWhiteSpace(proxyArg) ? null : new WebProxy(proxyArg);
if (wsProxy is not null)
    Log("代理", $"WS 使用代理 {new Uri(proxyArg!).Host}:{new Uri(proxyArg!).Port}");

using var httpClient = new HttpClient();
await using var connector = new GateConnector(
    httpClient, TestnetBaseUrl, new GateCredentials(apiKey, apiSecret), wsUrl: TestnetWsUrl, wsProxy: wsProxy);

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
var filled = false;
if (instrument is not null && lastPrice is { } marketPrice)
{
    // fill 模式挂在市价上方穿越价差立即成交；默认模式半价远离市价
    var price = instrument.AlignPrice(marketPrice * (fillMode ? 1.05m : 0.5m));
    // Instrument 暂未携带 min_quote_amount（领域缺口，待补），按目标名义金额反推数量
    const decimal targetNotional = 5m;
    var minQty = instrument.MinQuantity > 0 ? instrument.MinQuantity : instrument.StepSize;
    var notionalQty = instrument.AlignQuantity(targetNotional / price);
    var quantity = notionalQty > minQty ? notionalQty : minQty;

    // fill 模式先订阅私有订单推送并等 WS 就绪，再下单（否则成交发生在订阅生效前，推送被错过）
    var wsReady = new TaskCompletionSource();
    var wsConnectingSeen = false;
    using var stateSub = fillMode
        ? connector.ConnectionStates.Subscribe(s =>
        {
            Log("WS状态", s.ToString());
            // REST 的 ConnectAsync 也会发 Connected，须见过 Connecting 后的 Connected 才是 WS 就绪
            if (s == ConnectionState.Connecting)
                wsConnectingSeen = true;
            if (s == ConnectionState.Connected && wsConnectingSeen)
                wsReady.TrySetResult();
        })
        : null;

    var orderUpdates = new List<SpotOrderUpdate>();
    using var updatesSub = fillMode
        ? connector.SpotOrderUpdates.Subscribe(
            u =>
            {
                lock (orderUpdates)
                    orderUpdates.Add(u);
                if (u.Order.OrderId == orderId)
                    Log("推送", $"OrderId={u.Order.OrderId} Status={u.Order.Status} FilledQuantity={u.Order.FilledQuantity}");
            },
            ex => Log("推送", $"错误：{ex.Message}")) // ack 鉴权失败等不再静默
        : null;

    if (fillMode)
    {
        try
        {
            await wsReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Log("WS状态", "私有订阅已就绪，开始下单");
        }
        catch (TimeoutException)
        {
            Log("WS状态", "等待连接超时");
            failed = true;
        }
    }

    Log("下单", $"请求：Limit Buy {gatePair} Price={price.ToString(CultureInfo.InvariantCulture)} Quantity={quantity.ToString(CultureInfo.InvariantCulture)}");

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

        if (fillMode)
        {
            // 同步成交的单子 REST 响应已带 Filled，推送只作额外验证；不得对已成订单撤单
            filled = order.Status == OrderStatus.Filled;

            var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(15);
            var pushConfirmed = false;
            while (DateTimeOffset.Now < deadline && !pushConfirmed)
            {
                lock (orderUpdates)
                    pushConfirmed = orderUpdates.Any(u => u.Order.OrderId == orderId && u.Order.Status == OrderStatus.Filled);
                if (!pushConfirmed)
                    await Task.Delay(200);
            }

            if (pushConfirmed)
                Log("推送", "OK：私有频道确认 Filled");
            else
            {
                Log("推送", "超时未收到成交推送");
                failed = true;
            }
        }
    }
}
else
{
    Log("下单", "跳过：前置步骤（Instruments/Ticker）失败");
    failed = true;
}

// ---------- 8. 撤单（fill 模式已成交则跳过，改为复查余额验证资产变化） ----------
if (orderId is not null && !filled)
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
else if (filled)
{
    var after = await connector.GetAccountAsync(CancellationToken.None);
    if (after.IsSuccess)
    {
        Log("复查", "成交后余额：");
        foreach (var a in after.Value!.Assets.Where(x => x.Total > 0))
            Log("复查", $"  {a.Asset}: Total={a.Total} Frozen={a.Frozen}");
    }

    Log("撤单", "跳过：订单已成交");
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
