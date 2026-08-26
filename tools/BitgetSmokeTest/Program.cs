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
using TradingClient.Exchanges.Bitget;
using TradingClient.Exchanges.Bitget.Auth;

// Bitget 模拟盘（demoTrading）现货冒烟测试：连接 → 余额 → 限价买单 → 撤单（默认）或立即成交（--fill）
// --fill 模式：订阅订单私有推送，价格挂在市价上方保证立即成交；Bitget 下单响应只回 orderId（Status 恒为 New），
// 成交判定的权威源是私有推送，确认 Filled 后复查余额并跳过撤单
// 模拟盘与生产同主机（api.bitget.com），差异在 paptrading 头与 wspap WS 端点，均由 demoTrading: true 处理
// 凭证只走环境变量，永不打印 secret/passphrase，key 只打前 4 位掩码（§9）

static void Log(string step, string message) =>
    Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [{step}] {message}");

// ---------- 1. 凭证 ----------
var apiKey = Environment.GetEnvironmentVariable("BITGET_TESTNET_API_KEY");
var apiSecret = Environment.GetEnvironmentVariable("BITGET_TESTNET_API_SECRET");
var passphrase = Environment.GetEnvironmentVariable("BITGET_TESTNET_PASSPHRASE");
if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret) || string.IsNullOrWhiteSpace(passphrase))
{
    Console.WriteLine("""
        Bitget 模拟盘现货冒烟测试

        用法:
          设置环境变量后运行:
            BITGET_TESTNET_API_KEY=<api key>
            BITGET_TESTNET_API_SECRET=<api secret>
            BITGET_TESTNET_PASSPHRASE=<api passphrase>
          dotnet run --project tools/BitgetSmokeTest -- [PAIR] [--fill]

        参数:
          PAIR    Bitget 现货交易对，支持 BTCUSDT 或 BTC/USDT 格式，默认 BTCUSDT
          --fill  成交模式：价格挂在市价上方立即成交，以订单私有推送的 Filled 为成交判定依据

        可选:
          BITGET_TESTNET_PROXY / HTTPS_PROXY   同时用于 WS 与 REST 的代理（部分网络环境访问 api.bitget.com 需要）

        流程: 连接（demoTrading）-> 查余额 -> 限价买单（默认远离市价不成交；--fill 立即成交）-> 撤单（--fill 成交后跳过）
        """);
    return 2;
}

var maskedKey = apiKey.Length >= 4 ? apiKey[..4] + "****" : "****";
Log("凭证", $"已读取环境变量，ApiKey={maskedKey}");

// ---------- 2. 交易对参数与模式 ----------
var fillMode = args.Contains("--fill");
var pairArg = args.Length > 0 && args[0] != "--fill" ? args[0] : "BTCUSDT";
SpotSymbol symbol;
try
{
    // 接受 BTC/USDT 或 BTCUSDT：去掉分隔符后走适配器的后缀解析
    symbol = BitgetSymbolFormatter.ParseSpot(pairArg.Replace("/", ""));
}
catch (ArgumentException)
{
    Log("参数", $"失败：无法解析交易对 '{pairArg}'，期望格式 BTCUSDT 或 BTC/USDT");
    return 1;
}
var bitgetPair = BitgetSymbolFormatter.FormatSpot(symbol);
Log("参数", $"目标交易对 {bitgetPair}（{(fillMode ? "fill 模式：市价上方挂单立即成交" : "默认模式：半价挂单后撤单")}）");

var failed = false;
// 代理（可选）：部分网络环境访问 api.bitget.com 需要代理，WS 与 REST（含公共 HttpClient）同走一个代理
var proxyArg = Environment.GetEnvironmentVariable("BITGET_TESTNET_PROXY") ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
var proxy = string.IsNullOrWhiteSpace(proxyArg) ? null : new WebProxy(proxyArg);
if (proxy is not null)
    Log("代理", $"WS/REST 使用代理 {new Uri(proxyArg!).Host}:{new Uri(proxyArg!).Port}");

using var httpClient = proxy is not null
    ? new HttpClient(new HttpClientHandler { Proxy = proxy })
    : new HttpClient();
await using var connector = new BitgetConnector(
    httpClient,
    credentials: new BitgetCredentials(apiKey, apiSecret, passphrase),
    demoTrading: true,
    wsProxy: proxy,
    httpProxy: proxy);

// ---------- 3. 连接（内含 /v2/public/time 校时，失败降级本地时钟） ----------
try
{
    await connector.ConnectAsync(CancellationToken.None);
    Log("连接", $"成功（模拟盘: {BitgetConnector.DefaultBaseUrl}，WS: {BitgetConnector.DemoWsUrl}）");
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
        Log("Instruments", $"失败：找不到 {bitgetPair}");
        failed = true;
    }
    else if (instrument.Status != InstrumentStatus.Trading)
    {
        Log("Instruments", $"失败：{bitgetPair} 状态为 {instrument.Status}，不可交易");
        failed = true;
    }
    else
    {
        Log("Instruments", $"OK：TickSize={instrument.TickSize} StepSize={instrument.StepSize} MinQuantity={instrument.MinQuantity} MinQuoteAmount={instrument.MinQuoteAmount}");
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
    // V3 信封包数组，取 data[0].lastPrice
    var envelope = await httpClient.GetFromJsonAsync(
        $"{BitgetConnector.DefaultBaseUrl}/api/v3/market/tickers?category=SPOT&symbol={bitgetPair}",
        SmokeJsonContext.Default.BitgetTickerEnvelope);
    var last = envelope?.Data is { Length: > 0 } data ? data[0].LastPrice : null;
    if (last is null || !decimal.TryParse(last, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
    {
        Log("Ticker", $"失败：{bitgetPair} 无有效最新价（lastPrice='{last ?? "<null>"}' code='{envelope?.Code ?? "<null>"}'）");
        failed = true;
    }
    else
    {
        lastPrice = parsed;
        Log("Ticker", $"OK：{bitgetPair} last={parsed.ToString(CultureInfo.InvariantCulture)}");
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
    Log("余额", $"OK：{assets.Count} 个币种（TotalEquity={account.Value.TotalEquity}）");
    foreach (var a in assets)
        Log("余额", $"  {a.Asset}: Total={a.Total} Frozen={a.Frozen}");
}

// ---------- 7. 下单（走用例层，顺带验证编排与对齐校验） ----------
string? orderId = null;
var filled = false;
if (instrument is not null && lastPrice is { } marketPrice)
{
    // fill 模式挂在市价上方穿越价差立即成交；默认模式半价远离市价。
    // 买单加价幅度必须落在价格带内：Bitget 限价买单价格不得高于市价 buyLimitPriceRatio（BTC 实测 2%，超出拒单 25206）
    var price = instrument.AlignPrice(marketPrice * (fillMode ? 1.01m : 0.5m));
    // 按目标名义金额反推数量：覆盖交易所 MinQuoteAmount（minOrderAmount）并留 20% 余量
    var targetNotional = instrument.MinQuoteAmount is { } minQuote
        ? decimal.Max(5m, minQuote * 1.2m)
        : 5m;
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
            ex => Log("推送", $"错误：{ex.Message}")) // login 鉴权失败等不再静默
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

    Log("下单", $"请求：Limit Buy {bitgetPair} Price={price.ToString(CultureInfo.InvariantCulture)} Quantity={quantity.ToString(CultureInfo.InvariantCulture)}");

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
        // Bitget 下单响应只回 orderId，Status 恒为 New，不代表未成交
        Log("下单", $"OK：OrderId={order.OrderId} Status={order.Status}（响应不含成交状态，以私有推送为准）");

        if (fillMode)
        {
            // 成交判定权威源是私有推送：轮询等该 orderId 的 Filled 推送
            var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(15);
            while (DateTimeOffset.Now < deadline && !filled)
            {
                lock (orderUpdates)
                    filled = orderUpdates.Any(u => u.Order.OrderId == orderId && u.Order.Status == OrderStatus.Filled);
                if (!filled)
                    await Task.Delay(200);
            }

            if (filled)
                Log("推送", "OK：私有频道确认 Filled");
            else
            {
                // 超时兜底：交由后面的撤单步骤处理，避免挂单遗留
                Log("推送", "超时未收到成交推送，将尝试撤单兜底");
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

// ---------- 8. 撤单（fill 模式已成交则跳过，改为复查余额验证资产变化；成交推送超时时本步即兜底撤单） ----------
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

// Bitget V3 公共响应 DTO：定义在本工具内，不动 Exchanges.Bitget 的 internal DTO
internal sealed record BitgetTickerEnvelope(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("msg")] string? Msg,
    [property: JsonPropertyName("data")] BitgetTicker[]? Data);

internal sealed record BitgetTicker(
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("lastPrice")] string? LastPrice);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BitgetTickerEnvelope))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;
