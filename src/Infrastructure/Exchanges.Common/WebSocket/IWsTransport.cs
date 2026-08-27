namespace TradingClient.Exchanges.Common;

/// <summary>
/// WS 帧收发抽象：订阅管理与解析逻辑只依赖本接口，
/// 单测用内存假实现回放录制消息，不依赖真实 WS 服务器。
/// ConnectAsync 可重复调用（断线重连时重建底层连接）
/// </summary>
public interface IWsTransport : IDisposable
{
    Task ConnectAsync(Uri endpoint, CancellationToken ct);

    Task SendAsync(string message, CancellationToken ct);

    /// <summary>返回 null 表示连接被服务端关闭或传输出错，由上层决定重连</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>强制断开当前连接，用于收到服务端升级通知后触发重连</summary>
    void Abort();
}
