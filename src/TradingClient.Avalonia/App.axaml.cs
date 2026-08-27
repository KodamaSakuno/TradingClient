using System.Net;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using TradingClient.Application.Abstractions;
using TradingClient.Application.Risk;
using TradingClient.Application.Risk.Evaluators;
using TradingClient.Application.Risk.Rules;
using TradingClient.Application.Services;
using TradingClient.Application.UseCases.Futures;
using TradingClient.Application.UseCases.Spot;
using TradingClient.Avalonia.Risk;
using TradingClient.Avalonia.ViewModels;
using TradingClient.Avalonia.Views;
using TradingClient.Exchanges.Bitget;
using TradingClient.Exchanges.Bitget.Auth;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;
using TradingClient.Persistence;

namespace TradingClient.Avalonia;

// 基类全限定：本程序集内 using TradingClient.Application.* 会让 Application 解析到命名空间
public sealed class App : global::Avalonia.Application
{
    // Gate testnet 端点（将来改走配置文件 §9）
    private const string TestnetBaseUrl = "https://api-testnet.gateapi.io";
    private const string TestnetWsUrl = "wss://ws-testnet.gate.com/v4/ws/spot";
    // 期货是独立 testnet WS 端点；不传会落默认生产端点 fx-ws，testnet 凭证鉴权私有频道必被拒
    private const string TestnetFuturesWsUrl = "wss://ws-testnet.gate.com/v4/ws/futures/usdt";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = ConfigureServices();

            // 事中监控启动：订阅持仓/行情/连接流（§6.4）
            services.GetRequiredService<RiskMonitor>().Start();

            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // ServiceProvider 内含 IAsyncDisposable 连接器（GateConnector/BitgetConnector），退出时必须异步释放。
            // 库代码无 ConfigureAwait(false)，在 UI 线程直接等会捕获 Avalonia 同步上下文形成死锁（进程残留），
            // 故经 Task.Run 移出 UI 线程再同步等
            desktop.Exit += (_, _) =>
            {
                (desktop.MainWindow?.DataContext as IDisposable)?.Dispose();
                Task.Run(() => services.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Composition Root（§8.4）：全部对象图在此组装，ViewModel 不得直接 new 连接器
    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILogger>(Log.Logger);
        services.AddSingleton(new HttpClient());

        // 凭证只走环境变量（§9），缺失时为 null：连接器照常启动，鉴权接口返回 MISSING_CREDENTIALS
        // 不进 DI 容器：AddSingleton<T> 的 class 约束不接受可空引用类型，凭证也只需在组装连接器时读一次
        var apiKey = Environment.GetEnvironmentVariable("GATE_TESTNET_API_KEY");
        var apiSecret = Environment.GetEnvironmentVariable("GATE_TESTNET_API_SECRET");
        var credentials = !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret)
            ? new GateCredentials(apiKey, apiSecret)
            : null;
        Log.Logger.Information(
            credentials is null
                ? "Gate testnet credentials not configured; authenticated endpoints will report MISSING_CREDENTIALS"
                : "Gate testnet credentials loaded (ApiKey={MaskedApiKey})",
            apiKey is null ? null : apiKey[..Math.Min(4, apiKey.Length)] + "****");

        services.AddSingleton(sp =>
        {
            // testnet 的 WS 端点在部分网络环境下需要代理，与 tools/GateSmokeTest 同一约定
            var proxyArg = Environment.GetEnvironmentVariable("GATE_TESTNET_PROXY")
                ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
            var wsProxy = string.IsNullOrWhiteSpace(proxyArg) ? null : new WebProxy(proxyArg);

            return new GateConnector(
                sp.GetRequiredService<HttpClient>(),
                TestnetBaseUrl,
                credentials,
                wsUrl: TestnetWsUrl,
                wsProxy: wsProxy,
                futuresWsUrl: TestnetFuturesWsUrl,
                // 死 man's switch（§6.4）：10s 续期是演示值；客户端死亡的交易所侧兜底，
                // 与 RiskMonitor 的断线主动撤单是两层防线
                futuresDeadManInterval: TimeSpan.FromSeconds(10));
        });

        // Bitget 凭证三字段缺一即降级为 null，与 Gate 同款
        var bitgetApiKey = Environment.GetEnvironmentVariable("BITGET_TESTNET_API_KEY");
        var bitgetApiSecret = Environment.GetEnvironmentVariable("BITGET_TESTNET_API_SECRET");
        var bitgetPassphrase = Environment.GetEnvironmentVariable("BITGET_TESTNET_PASSPHRASE");
        var bitgetCredentials = !string.IsNullOrWhiteSpace(bitgetApiKey)
            && !string.IsNullOrWhiteSpace(bitgetApiSecret)
            && !string.IsNullOrWhiteSpace(bitgetPassphrase)
            ? new BitgetCredentials(bitgetApiKey, bitgetApiSecret, bitgetPassphrase)
            : null;
        Log.Logger.Information(
            bitgetCredentials is null
                ? "Bitget demo credentials not configured; authenticated endpoints will report MISSING_CREDENTIALS"
                : "Bitget demo credentials loaded (ApiKey={MaskedApiKey})",
            bitgetApiKey is null ? null : bitgetApiKey[..Math.Min(4, bitgetApiKey.Length)] + "****");

        services.AddSingleton(sp =>
        {
            var proxyArg = Environment.GetEnvironmentVariable("BITGET_TESTNET_PROXY")
                ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
            var proxy = string.IsNullOrWhiteSpace(proxyArg) ? null : new WebProxy(proxyArg);

            // 模拟盘与生产共用 REST 主机（用默认 baseUrl），差异在 paptrading 请求头与 wspap WS 端点，
            // 由 demoTrading 切换（BitgetConnector 内部按 demoTrading 选 wspap 公共/私有端点）
            return new BitgetConnector(
                sp.GetRequiredService<HttpClient>(),
                credentials: bitgetCredentials,
                demoTrading: true,
                wsProxy: proxy,
                httpProxy: proxy);
        });

        // 同一连接器实例按能力面向不同抽象注册（§5.1）
        // 注意：MS DI 单服务解析取最后注册项——共享抽象（IMarketData 等）仍指向 Gate，
        // 供 PlaceSpotOrder 等现有用例使用；多连接器的按选择器分派等下单 UI 接入时再设计
        services.AddSingleton<IExchangeConnector>(sp => sp.GetRequiredService<GateConnector>());
        services.AddSingleton<IMarketData>(sp => sp.GetRequiredService<GateConnector>());
        services.AddSingleton<IAccountService>(sp => sp.GetRequiredService<GateConnector>());
        services.AddSingleton<ISpotTrading>(sp => sp.GetRequiredService<GateConnector>());

        services.AddSingleton(sp =>
        {
            var registry = new ExchangeRegistry();
            registry.Register(sp.GetRequiredService<GateConnector>());
            registry.Register(sp.GetRequiredService<BitgetConnector>());
            return registry;
        });

        services.AddSingleton(sp => new InstrumentCache(sp.GetRequiredService<IMarketData>()));

        // 下单前风控链（§6.4）。限额配置存本地 JSON 文件，将来随 §9.1 迁 SQLite/PostgreSQL；
        // 文件不存在时用内置演示默认值（真实限额应由用户按账户规模配置）。
        // 组装时加载一次，运行时改配置重启生效
        services.AddSingleton<IRiskLimitsStore>(
            new JsonRiskLimitsStore(Path.Combine(AppContext.BaseDirectory, "risk-limits.json")));
        services.AddSingleton<IRiskAuditSink, SerilogRiskAuditSink>();
        // 风控状态机共享单例（§6.4）：事前闸门读它，事中 RiskMonitor 写它
        services.AddSingleton<RiskStateMachine>();
        services.AddSingleton(sp =>
            sp.GetRequiredService<IRiskLimitsStore>().LoadAsync(CancellationToken.None)
                .GetAwaiter().GetResult()
            ?? new RiskLimitsProfile(
                new RiskRuleConfig(
                    MaxOrderQuantity: 1m,
                    MaxDailyQuantity: 10m,
                    MaxPositionQuantity: 5m,
                    MaxPriceDeviationRatio: 0.05m,
                    DuplicatePriceToleranceRatio: 0.001,
                    DuplicateWindow: TimeSpan.FromSeconds(3)),
                PerSymbol: new Dictionary<string, RiskRuleConfig>()));
        services.AddSingleton(sp => new PreTradeRiskChain(
            [
                // 状态闸门排最前：状态级检查最便宜
                new RiskStateGateRule(sp.GetRequiredService<RiskStateMachine>()),
                new ConnectionGuardRule(),
                new OrderSizeLimitRule(sp.GetRequiredService<RiskLimitsProfile>()),
                new DailyVolumeLimitRule(sp.GetRequiredService<RiskLimitsProfile>(), TimeProvider.System),
                new PositionLimitRule(sp.GetRequiredService<RiskLimitsProfile>()),
                new PriceDeviationRule(sp.GetRequiredService<RiskLimitsProfile>()),
                new DuplicateOrderRule(sp.GetRequiredService<RiskLimitsProfile>(), TimeProvider.System),
            ],
            sp.GetRequiredService<IRiskAuditSink>()));

        services.AddSingleton(sp => new PlaceSpotOrder(
            sp.GetRequiredService<ISpotTrading>(),
            sp.GetRequiredService<InstrumentCache>(),
            sp.GetRequiredService<PreTradeRiskChain>(),
            sp.GetRequiredService<IRiskSnapshotSource>()));
        services.AddSingleton(sp => new CancelSpotOrder(sp.GetRequiredService<ISpotTrading>()));
        services.AddSingleton<IFuturesTrading>(sp => sp.GetRequiredService<GateConnector>());
        services.AddSingleton(sp => new PlaceFuturesOrder(
            sp.GetRequiredService<IFuturesTrading>(),
            sp.GetRequiredService<InstrumentCache>(),
            sp.GetRequiredService<PreTradeRiskChain>(),
            sp.GetRequiredService<IRiskSnapshotSource>()));

        // 事中风险监控（§6.4 第二层）：评估器可插拔，阈值读 RiskMonitorConfig
        services.AddSingleton<IReadOnlyList<IRiskEvaluator>>(sp =>
        {
            var monitorConfig = sp.GetRequiredService<RiskLimitsProfile>().MonitorOrDefault;
            return
            [
                new DailyLossCircuitBreaker(monitorConfig),
                new TotalExposureLimitEvaluator(monitorConfig),
            ];
        });
        // 门面单解析：IFuturesTrading/IMarketData 当前都指向 Gate，监控 Gate 账户；
        // 多连接器监控的分派设计留待交易所选择器落地后再做
        services.AddSingleton(sp => new RiskMonitor(
            sp.GetRequiredService<IFuturesTrading>(),
            sp.GetRequiredService<IMarketData>(),
            sp.GetRequiredService<RiskStateMachine>(),
            sp.GetRequiredService<IReadOnlyList<IRiskEvaluator>>(),
            sp.GetRequiredService<IRiskAuditSink>(),
            sp.GetRequiredService<RiskLimitsProfile>().MonitorOrDefault,
            TimeProvider.System));
        // RiskMonitor 同时是事前链的快照源（§6.4）：其持仓/最新价表供下单用例组装 RiskCheckContext。
        // 快照按 Symbol.Raw 精确匹配，现货 Symbol 不在监控表内 → 恒 null、规则跳过，天然正确；
        // 多连接器监控分派（含快照源按交易所路由）与门面分派欠账一起留待后续
        services.AddSingleton<IRiskSnapshotSource>(sp => sp.GetRequiredService<RiskMonitor>());

        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
