using System.Net;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using TradingClient.Application.Abstractions;
using TradingClient.Application.Services;
using TradingClient.Application.UseCases.Spot;
using TradingClient.Avalonia.ViewModels;
using TradingClient.Avalonia.Views;
using TradingClient.Exchanges.Gate;
using TradingClient.Exchanges.Gate.Auth;

namespace TradingClient.Avalonia;

// 基类全限定：本程序集内 using TradingClient.Application.* 会让 Application 解析到命名空间
public sealed class App : global::Avalonia.Application
{
    // Gate testnet 端点（当前唯一连接器；将来改走配置文件 §9）
    private const string TestnetBaseUrl = "https://api-testnet.gateapi.io";
    private const string TestnetWsUrl = "wss://ws-testnet.gate.com/v4/ws/spot";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = ConfigureServices();

            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // 连接与首屏数据拉取是后台初始化，失败已在内部记录并降级，不阻塞启动
            _ = viewModel.InitializeAsync();

            // ServiceProvider 内含 IAsyncDisposable 连接器（GateConnector），退出时必须异步释放
            desktop.Exit += (_, _) => services.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
                wsProxy: wsProxy);
        });

        // 同一连接器实例按能力面向不同抽象注册（§5.1）
        services.AddSingleton<IExchangeConnector>(sp => sp.GetRequiredService<GateConnector>());
        services.AddSingleton<IMarketData>(sp => sp.GetRequiredService<GateConnector>());
        services.AddSingleton<IAccountService>(sp => sp.GetRequiredService<GateConnector>());
        services.AddSingleton<ISpotTrading>(sp => sp.GetRequiredService<GateConnector>());

        services.AddSingleton(sp =>
        {
            var registry = new ExchangeRegistry();
            registry.Register(sp.GetRequiredService<GateConnector>());
            return registry;
        });

        services.AddSingleton(sp => new InstrumentCache(sp.GetRequiredService<IMarketData>()));
        services.AddSingleton(sp => new PlaceSpotOrder(
            sp.GetRequiredService<ISpotTrading>(), sp.GetRequiredService<InstrumentCache>()));
        services.AddSingleton(sp => new CancelSpotOrder(sp.GetRequiredService<ISpotTrading>()));

        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
