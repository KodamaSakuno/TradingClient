using System.Globalization;
using TradingClient.Exchanges.Common;

namespace TradingClient.Exchanges.Bitget.Auth;

/// <summary>
/// 给出站请求附加 Bitget 鉴权头 ACCESS-KEY / ACCESS-SIGN / ACCESS-TIMESTAMP / ACCESS-PASSPHRASE。
/// 模拟盘（demoTrading）额外加 paptrading: 1（.local/bitget/uta/demo-trading/rest-api.md）。
/// </summary>
internal sealed class BitgetAuthHandler(BitgetCredentials credentials, ServerTimeSync timeSync, bool demoTrading) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Bitget signed request requires RequestUri.");

        // ReadAsStringAsync 会将 Content 缓冲进内存，不影响后续实际发送
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        var timestamp = timeSync.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var sign = BitgetSigner.Sign(
            credentials.ApiSecret,
            timestamp,
            request.Method.Method,
            uri.AbsolutePath,
            uri.Query.TrimStart('?'),
            body);

        request.Headers.TryAddWithoutValidation("ACCESS-KEY", credentials.ApiKey);
        request.Headers.TryAddWithoutValidation("ACCESS-SIGN", sign);
        request.Headers.TryAddWithoutValidation("ACCESS-TIMESTAMP", timestamp);
        request.Headers.TryAddWithoutValidation("ACCESS-PASSPHRASE", credentials.Passphrase);
        if (demoTrading)
            request.Headers.TryAddWithoutValidation("paptrading", "1");

        return await base.SendAsync(request, cancellationToken);
    }
}
