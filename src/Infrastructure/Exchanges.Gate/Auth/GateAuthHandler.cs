using System.Globalization;
using TradingClient.Exchanges.Common;

namespace TradingClient.Exchanges.Gate.Auth;

/// <summary>给出站请求附加 Gate APIv4 鉴权头 KEY / Timestamp / SIGN。</summary>
internal sealed class GateAuthHandler(GateCredentials credentials, ServerTimeSync timeSync) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Gate signed request requires RequestUri.");

        // ReadAsStringAsync 会将 Content 缓冲进内存，不影响后续实际发送
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        var timestamp = timeSync.UtcNow.ToUnixTimeSeconds();
        var sign = GateSigner.Sign(
            credentials.ApiSecret,
            request.Method.Method,
            uri.AbsolutePath,
            uri.Query.TrimStart('?'),
            body,
            timestamp);

        request.Headers.TryAddWithoutValidation("KEY", credentials.ApiKey);
        request.Headers.TryAddWithoutValidation("Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("SIGN", sign);

        return await base.SendAsync(request, cancellationToken);
    }
}
