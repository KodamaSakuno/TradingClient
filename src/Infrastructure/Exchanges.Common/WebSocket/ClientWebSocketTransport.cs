using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace TradingClient.Exchanges.Common;

public sealed class ClientWebSocketTransport(IWebProxy? proxy = null) : IWsTransport
{
    private ClientWebSocket? _socket;

    public async Task ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        Dispose();
        _socket = new ClientWebSocket();
        if (proxy is not null)
            _socket.Options.Proxy = proxy;
        await _socket.ConnectAsync(endpoint, ct);
    }

    public async Task SendAsync(string message, CancellationToken ct)
    {
        var socket = _socket ?? throw new InvalidOperationException("WebSocket is not connected.");
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null)
            return null;

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var writer = new ArrayBufferWriter<byte>(8192);

        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    }
                    catch (WebSocketException)
                    {
                        // 回执关闭帧失败不影响断线判定
                    }

                    return null;
                }

                writer.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(writer.WrittenSpan);
            }
        }
        catch (WebSocketException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Abort()
    {
        try
        {
            _socket?.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _socket?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _socket = null;
    }
}
