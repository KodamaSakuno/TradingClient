using Avalonia;
using Avalonia.ReactiveUI;
using Serilog;

namespace TradingClient.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Serilog 最小配置：本步只接 console sink，结构化落盘后续接入
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Avalonia 设计器与 XAML 预览器依赖此方法
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .UseReactiveUI()
        .LogToTrace();
}
