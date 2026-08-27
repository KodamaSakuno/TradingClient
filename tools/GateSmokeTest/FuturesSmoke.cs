using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using TradingClient.Application.Abstractions;
using TradingClient.Application.Services;
using TradingClient.Domain.Instruments;
using TradingClient.Domain.Trading;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;

// Gate testnet 期货全链路冒烟（--futures）：连接 → instruments → ticker → 订阅持仓私有推送
// → 设杠杆 10x 全仓 → 限价吃 ask1 开多 → 持仓推送确认 → 限价吃 bid1 平仓 → 复查持仓归零
// 不用市价单：testnet 盘口常失真（ask1 偏离 mark 超 2%），市价单保护价够不到 ask1 会被拒
// （PRICE_TOO_DEVIATED）；合约 order_price_deviate=0.5，限价单吃对手价在偏离带内
// 凭证/代理环境变量、输出格式、退出码约定与现货模式（Program.cs）一致
internal static class FuturesSmoke
{
    // REST 与现货 testnet 同域；期货 WS 是独立端点（usdt settle，注意不是旧域名 fx-ws-testnet）
    private const string TestnetBaseUrl = "https://api-testnet.gateapi.io";
    private const string TestnetWsUrl = "wss://ws-testnet.gate.com/v4/ws/spot";
    private const string TestnetFuturesWsUrl = "wss://ws-testnet.gate.com/v4/ws/futures/usdt";

    private static void Log(string step, string message) =>
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [{step}] {message}");

    public static async Task<int> RunAsync(bool dualMode)
    {
        // ---------- 1. 凭证 ----------
        var apiKey = Environment.GetEnvironmentVariable("GATE_TESTNET_API_KEY");
        var apiSecret = Environment.GetEnvironmentVariable("GATE_TESTNET_API_SECRET");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            Console.WriteLine("""
                Gate testnet 期货全链路冒烟测试

                用法:
                  设置环境变量后运行:
                    GATE_TESTNET_API_KEY=<testnet api key>
                    GATE_TESTNET_API_SECRET=<testnet api secret>
                  dotnet run --project tools/GateSmokeTest -- --futures

                流程: 连接 testnet -> BTC_USDT 永续 instruments -> ticker -> 订阅持仓私有推送
                      -> 设杠杆 10x 全仓 -> 限价吃 ask1 开多 -> 持仓确认 -> 限价吃 bid1 平仓 -> 复查持仓归零
                选项: --dual  双向持仓链路：切 dual -> 开多开空 -> 双腿验证 -> 全平 -> 复查归零（finally 复原 single）
                """);
            return 2;
        }

        var maskedKey = apiKey.Length >= 4 ? apiKey[..4] + "****" : "****";
        Log("凭证", $"已读取环境变量，ApiKey={maskedKey}");

        var symbol = new PerpetualFuturesSymbol("BTC", "USDT");
        const string contract = "BTC_USDT";
        var failed = false;

        // WS 代理（可选）：与现货模式同一约定；期货 WS 未给独立 transport，复用现货 transport 工厂，代理自动生效
        var proxyArg = Environment.GetEnvironmentVariable("GATE_TESTNET_PROXY") ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var wsProxy = string.IsNullOrWhiteSpace(proxyArg) ? null : new WebProxy(proxyArg);
        if (wsProxy is not null)
            Log("代理", $"WS 使用代理 {new Uri(proxyArg!).Host}:{new Uri(proxyArg!).Port}");

        using var httpClient = new HttpClient();
        await using var connector = new GateConnector(
            httpClient, TestnetBaseUrl, new GateCredentials(apiKey, apiSecret),
            wsUrl: TestnetWsUrl, wsProxy: wsProxy, futuresWsUrl: TestnetFuturesWsUrl);

        // ---------- 2. 连接（内含 /spot/time 校时，失败降级本地时钟；期货 WS 在订阅推送时才连接） ----------
        try
        {
            await connector.ConnectAsync(CancellationToken.None);
            Log("连接", $"成功（testnet: {TestnetBaseUrl}，期货 WS: {TestnetFuturesWsUrl}）");
        }
        catch (Exception ex)
        {
            Log("连接", $"失败：{ex.Message}");
            return 1;
        }

        // ---------- 3. instruments：找 BTC_USDT 永续（顺带填充适配器的张→币乘数缓存） ----------
        var instruments = new InstrumentCache(connector);
        Instrument? instrument;
        try
        {
            await instruments.RefreshAsync(ProductKind.Futures, CancellationToken.None);
            instrument = await instruments.GetAsync(symbol, CancellationToken.None);
            if (instrument is null)
            {
                Log("Instruments", $"失败：testnet 上找不到 {contract} 永续");
                return 1;
            }
            Log("Instruments", $"OK：TickSize={instrument.TickSize} StepSize={instrument.StepSize} MinQuantity={instrument.MinQuantity} ContractMultiplier={instrument.ContractMultiplier}");
            if (instrument.Status != InstrumentStatus.Trading)
            {
                Log("Instruments", $"失败：{contract} 状态为 {instrument.Status}，不可交易");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Log("Instruments", $"失败：{ex.Message}");
            return 1;
        }

        // ---------- 4. 公共 REST ticker 取最新价（与现货模式同法：自供 httpClient，不经连接器） ----------
        try
        {
            var tickers = await httpClient.GetFromJsonAsync(
                $"{TestnetBaseUrl}/api/v4/futures/usdt/tickers?contract={contract}",
                SmokeJsonContext.Default.GateTickerArray);
            var last = tickers is { Length: > 0 } ? tickers[0].Last : null;
            if (last is null || !decimal.TryParse(last, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                Log("Ticker", $"失败：{contract} 无有效最新价（last='{last ?? "<null>"}'）");
                return 1;
            }
            Log("Ticker", $"OK：{contract} last={parsed.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex)
        {
            Log("Ticker", $"失败：{ex.Message}");
            return 1;
        }

        // ---------- 5. 订阅持仓/强平私有推送，等期货 WS 就绪再下单（否则成交推送可能错过） ----------
        var positionUpdates = new List<PositionUpdate>();
        var wsReady = new TaskCompletionSource();
        var wsConnectingSeen = false;
        using var stateSub = connector.ConnectionStates.Subscribe(s =>
        {
            Log("WS状态", s.ToString());
            // REST 的 ConnectAsync 已发过一次 Connected，须见过 Connecting 后的 Connected 才是期货 WS 就绪
            if (s == ConnectionState.Connecting)
                wsConnectingSeen = true;
            if (s == ConnectionState.Connected && wsConnectingSeen)
                wsReady.TrySetResult();
        });
        using var positionSub = connector.PositionUpdates.Subscribe(
            u =>
            {
                lock (positionUpdates)
                    positionUpdates.Add(u);
                var p = u.Position;
                Log("持仓推送", $"{p.Symbol.Raw} {p.Side} Quantity={p.Quantity} EntryPrice={p.EntryPrice}");
            },
            ex => Log("持仓推送", $"错误：{ex.Message}"));
        // 强平预警只打印不判定：正常链路不会触发，触发说明本地估算公式有问题
        using var liqSub = connector.LiquidationWarnings.Subscribe(
            w => Log("强平预警", $"触发（仅打印）：{w.Symbol.Raw} {w.Side} EstimatedLiquidationPrice={w.EstimatedLiquidationPrice} MarginRatio={w.MarginRatio}"),
            ex => Log("强平预警", $"错误：{ex.Message}"));

        try
        {
            await wsReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Log("WS状态", "期货私有订阅已就绪，开始下单");
        }
        catch (TimeoutException)
        {
            Log("WS状态", "等待连接超时");
            return 1;
        }

        // ---------- 6-dual. --dual 分流：双向持仓链路（finally 复原 single），以下为单向持仓流程 ----------
        if (dualMode)
            return await RunDualFlowAsync(connector, httpClient, symbol, contract, instrument.MinQuantity);

        // ---------- 6. 设杠杆 10x 全仓 ----------
        var leverage = await connector.SetLeverageAsync(symbol, 10, MarginMode.Cross, CancellationToken.None);
        if (!leverage.IsSuccess)
        {
            Log("杠杆", $"失败：[{leverage.Error!.Code}] {leverage.Error.Message}");
            return 1;
        }
        Log("杠杆", "OK：Leverage=10 MarginMode=Cross");

        // ---------- 7. 限价吃 ask1 开多（数量为币，适配器内换算张） ----------
        var quantity = instrument.MinQuantity;
        decimal openPrice;
        try
        {
            var ask1 = await GetBestPriceAsync(httpClient, contract, OrderSide.Buy);
            if (ask1 is null)
            {
                Log("下单", "失败：盘口卖侧为空，无法吃 ask1");
                return 1;
            }
            openPrice = ask1.Value;
        }
        catch (Exception ex)
        {
            Log("下单", $"失败：取盘口异常 {ex.Message}");
            return 1;
        }
        Log("下单", $"请求：Limit Buy {contract} Price={openPrice.ToString(CultureInfo.InvariantCulture)}（盘口 ask1）Quantity={quantity.ToString(CultureInfo.InvariantCulture)} PositionSide=Long");
        var opened = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(symbol, OrderSide.Buy, OrderType.Limit, openPrice, quantity, PositionSide.Long, MarginMode.Cross, Leverage: null),
            CancellationToken.None);
        if (!opened.IsSuccess)
        {
            Log("下单", $"失败：[{opened.Error!.Code}] {opened.Error.Message}");
            return 1;
        }
        var openOrder = opened.Value!;
        Log("下单", $"OK：OrderId={openOrder.OrderId} Status={openOrder.Status}");

        // ---------- 8. 等持仓推送确认开多（15s；超时记失败但继续走平仓兜底） ----------
        var openConfirmed = await WaitForPositionAsync(
            positionUpdates, symbol, p => p is { Side: PositionSide.Long, Quantity: > 0 }, TimeSpan.FromSeconds(15));
        if (openConfirmed)
        {
            Log("持仓确认", $"OK：{contract} Long 持仓已出现");
        }
        else
        {
            Log("持仓确认", "超时未等到开多持仓推送");
            failed = true;
        }

        // ---------- 9. 限价吃 bid1 平仓（单向持仓模式 Sell 即减多仓） ----------
        // 开多未成交且无持仓确认时不能平：此时 Sell 会开出空仓
        var hasPosition = openConfirmed || openOrder.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled;
        if (!hasPosition)
        {
            Log("平仓", "跳过：开多未成交，无持仓可平");
            failed = true;
        }
        else
        {
            decimal closePrice;
            try
            {
                var bid1 = await GetBestPriceAsync(httpClient, contract, OrderSide.Sell);
                if (bid1 is null)
                {
                    Log("平仓", "失败：盘口买侧为空，无法吃 bid1");
                    failed = true;
                    Log("结果", "冒烟测试未全部通过");
                    return 1;
                }
                closePrice = bid1.Value;
            }
            catch (Exception ex)
            {
                Log("平仓", $"失败：取盘口异常 {ex.Message}");
                Log("结果", "冒烟测试未全部通过");
                return 1;
            }
            Log("平仓", $"请求：Limit Sell {contract} Price={closePrice.ToString(CultureInfo.InvariantCulture)}（盘口 bid1）Quantity={quantity.ToString(CultureInfo.InvariantCulture)} PositionSide=Short");
            var closed = await connector.PlaceFuturesOrderAsync(
                new PlaceFuturesOrderRequest(symbol, OrderSide.Sell, OrderType.Limit, closePrice, quantity, PositionSide.Short, MarginMode.Cross, Leverage: null),
                CancellationToken.None);
            if (!closed.IsSuccess)
            {
                Log("平仓", $"失败：[{closed.Error!.Code}] {closed.Error.Message}");
                failed = true;
            }
            else
            {
                Log("平仓", $"OK：OrderId={closed.Value!.OrderId} Status={closed.Value!.Status}");

                // 先等推送归零；推送可能因时序丢失，超时后用 REST 复查兜底
                var flatConfirmed = await WaitForPositionAsync(
                    positionUpdates, symbol, p => p.Quantity == 0, TimeSpan.FromSeconds(15));
                if (!flatConfirmed)
                {
                    var positions = await connector.GetPositionsAsync(CancellationToken.None);
                    if (!positions.IsSuccess)
                    {
                        Log("复查", $"失败：[{positions.Error!.Code}] {positions.Error.Message}");
                    }
                    else
                    {
                        flatConfirmed = positions.Value!.All(p => !p.Symbol.Equals(symbol) || p.Quantity == 0);
                        Log("复查", flatConfirmed ? $"REST 复查：{contract} 无持仓" : $"REST 复查：{contract} 仍有持仓");
                    }
                }

                if (flatConfirmed)
                {
                    Log("平仓确认", $"OK：{contract} 持仓已归零");
                }
                else
                {
                    Log("平仓确认", "未确认持仓归零");
                    failed = true;
                }
            }
        }

        Log("结果", failed ? "冒烟测试未全部通过" : "全链路通过");
        return failed ? 1 : 0;
    }

    // ---------- 双向持仓（dual）链路：--futures --dual ----------
    // 切 dual → 吃 ask1 开多 → 吃 bid1 开空 → REST 验证两腿 → 反向单 reduce_only 全平 → REST 复查归零
    // 持仓模式是账户级状态，无论成败 finally 都复原 single，避免账户滞留 dual
    private static async Task<int> RunDualFlowAsync(
        GateConnector connector, HttpClient httpClient,
        PerpetualFuturesSymbol symbol, string contract, decimal quantity)
    {
        var failed = false;

        var setDual = await connector.SetPositionModeAsync(PositionMode.Dual, CancellationToken.None);
        if (!setDual.IsSuccess)
        {
            Log("持仓模式", $"切 dual 失败：[{setDual.Error!.Code}] {setDual.Error.Message}");
            return 1;
        }
        Log("持仓模式", "OK：已切换为 dual（双向持仓）");

        try
        {
            // 开两腿：Long+Buy 加多、Short+Sell 加空（taker 限价单，吃对手价）
            if (!await PlaceTakerAsync(connector, httpClient, symbol, contract, PositionSide.Long, OrderSide.Buy, quantity, "开多"))
                failed = true;
            if (!await PlaceTakerAsync(connector, httpClient, symbol, contract, PositionSide.Short, OrderSide.Sell, quantity, "开空"))
                failed = true;

            // REST 轮询验证两腿都在（dual 模式 positions 返回 dual_long/dual_short 两条）
            var legsOk = await WaitForPositionsAsync(connector, symbol,
                legs => legs.Any(p => p.Side == PositionSide.Long) && legs.Any(p => p.Side == PositionSide.Short),
                "持仓验证");
            if (legsOk)
            {
                Log("持仓验证", $"OK：{contract} Long/Short 两腿持仓都在");

                // 平仓：Long+Sell 减多、Short+Buy 减空（适配器在 dual 下自动带 reduce_only）
                if (!await PlaceTakerAsync(connector, httpClient, symbol, contract, PositionSide.Long, OrderSide.Sell, quantity, "平多"))
                    failed = true;
                if (!await PlaceTakerAsync(connector, httpClient, symbol, contract, PositionSide.Short, OrderSide.Buy, quantity, "平空"))
                    failed = true;

                if (await WaitForPositionsAsync(connector, symbol, legs => legs.Count == 0, "复查"))
                    Log("复查", $"OK：{contract} 两腿持仓已归零");
                else
                {
                    Log("复查", "未确认持仓归零");
                    failed = true;
                }
            }
            else
            {
                Log("持仓验证", "未确认两腿持仓，跳过平仓");
                failed = true;
            }
        }
        finally
        {
            var restore = await connector.SetPositionModeAsync(PositionMode.Single, CancellationToken.None);
            if (restore.IsSuccess)
            {
                Log("持仓模式", "已复原 single");
            }
            else
            {
                Log("持仓模式", $"复原 single 失败：[{restore.Error!.Code}] {restore.Error.Message}（账户可能滞留 dual，需手工处理）");
                failed = true;
            }
        }

        Log("结果", failed ? "双向持仓冒烟未全部通过" : "双向持仓全链路通过");
        return failed ? 1 : 0;
    }

    // taker 限价单：买单吃 ask1、卖单吃 bid1（同单向流程，不用市价单的原因见文件头注释）
    private static async Task<bool> PlaceTakerAsync(
        GateConnector connector, HttpClient httpClient,
        PerpetualFuturesSymbol symbol, string contract,
        PositionSide side, OrderSide orderSide, decimal quantity, string step)
    {
        decimal price;
        try
        {
            var best = await GetBestPriceAsync(httpClient, contract, orderSide);
            if (best is null)
            {
                Log(step, "失败：盘口对应侧为空");
                return false;
            }
            price = best.Value;
        }
        catch (Exception ex)
        {
            Log(step, $"失败：取盘口异常 {ex.Message}");
            return false;
        }

        Log(step, $"请求：Limit {orderSide} {contract} Price={price.ToString(CultureInfo.InvariantCulture)}（盘口{(orderSide == OrderSide.Buy ? " ask1" : " bid1")}）Quantity={quantity.ToString(CultureInfo.InvariantCulture)} PositionSide={side}");
        var result = await connector.PlaceFuturesOrderAsync(
            new PlaceFuturesOrderRequest(symbol, orderSide, OrderType.Limit, price, quantity, side, MarginMode.Cross, Leverage: null),
            CancellationToken.None);
        if (!result.IsSuccess)
        {
            Log(step, $"失败：[{result.Error!.Code}] {result.Error.Message}");
            return false;
        }
        Log(step, $"OK：OrderId={result.Value!.OrderId} Status={result.Value.Status}");
        return true;
    }

    // REST 轮询持仓直到谓词满足（taker 单成交后持仓落库有短暂延迟）；legs 只含本 symbol 的有效持仓
    private static async Task<bool> WaitForPositionsAsync(
        GateConnector connector, PerpetualFuturesSymbol symbol,
        Func<IReadOnlyList<Position>, bool> predicate, string step)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.Now < deadline)
        {
            var positions = await connector.GetPositionsAsync(CancellationToken.None);
            if (!positions.IsSuccess)
            {
                Log(step, $"失败：[{positions.Error!.Code}] {positions.Error.Message}");
                return false;
            }
            var legs = positions.Value!.Where(p => p.Symbol.Equals(symbol) && p.Quantity > 0).ToArray();
            if (predicate(legs))
                return true;
            await Task.Delay(500);
        }
        return false;
    }

    // 取盘口最优对手价：买单吃 ask1，卖单吃 bid1；对应侧为空或价格无效返回 null
    private static async Task<decimal?> GetBestPriceAsync(HttpClient httpClient, string contract, OrderSide side)
    {
        var book = await httpClient.GetFromJsonAsync(
            $"{TestnetBaseUrl}/api/v4/futures/usdt/order_book?contract={contract}",
            SmokeJsonContext.Default.GateFuturesOrderBook);
        var level = side == OrderSide.Buy
            ? book?.Asks?.FirstOrDefault()
            : book?.Bids?.FirstOrDefault();
        return level is not null
            && decimal.TryParse(level.Price, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            && price > 0
                ? price
                : null;
    }

    // 从调用时点之后的推送里找匹配持仓（忽略历史推送，避免订阅快照/平仓前的旧帧误判）
    private static async Task<bool> WaitForPositionAsync(
        List<PositionUpdate> updates,
        PerpetualFuturesSymbol symbol,
        Func<Position, bool> predicate,
        TimeSpan timeout)
    {
        int startIndex;
        lock (updates)
            startIndex = updates.Count;

        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            lock (updates)
            {
                for (var i = startIndex; i < updates.Count; i++)
                {
                    var p = updates[i].Position;
                    if (p.Symbol.Equals(symbol) && predicate(p))
                        return true;
                }
            }
            await Task.Delay(200);
        }
        return false;
    }
}
