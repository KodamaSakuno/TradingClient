using System.Security.Cryptography;
using System.Text;

namespace TradingClient.Exchanges.Gate.Auth;

/// <summary>
/// Gate APIv4 签名：HexEncode(HMAC_SHA512(secret, signatureString))，hex 小写。
/// signatureString = METHOD\nPATH\nQUERY\nHexEncode(SHA512(BODY))\nTIMESTAMP。
/// QUERY 不做 URL 解码，与请求 URL 中的拼接保持一致；无 query / body 时用空串。
/// </summary>
internal static class GateSigner
{
    public static string Sign(string apiSecret, string method, string path, string? query, string? body, long unixTimestamp)
    {
        var bodyHash = Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(body ?? string.Empty)));
        var signatureString = string.Join('\n', method.ToUpperInvariant(), path, query ?? string.Empty, bodyHash, unixTimestamp);

        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureString)));
    }

    // WS 私有频道签名串与 REST 不同：channel=<channel>&event=<event>&time=<time>（.local/gate_api_spot_ws.txt 鉴权一节）
    public static string SignWs(string apiSecret, string channel, string evt, long unixTimestamp)
    {
        var signatureString = $"channel={channel}&event={evt}&time={unixTimestamp}";

        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureString)));
    }
}
