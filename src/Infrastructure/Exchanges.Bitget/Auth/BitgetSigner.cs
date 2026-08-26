using System.Security.Cryptography;
using System.Text;

namespace TradingClient.Exchanges.Bitget.Auth;

/// <summary>
/// Bitget V3 签名：Base64(HMAC_SHA256(secret, signatureString))（.local/bitget/uta/rest-api.md「签名」节）。
/// signatureString = TIMESTAMP + METHOD大写 + PATH + (QUERY 非空时 "?" + QUERY) + BODY。
/// QUERY 不做 URL 解码，与请求 URL 中的拼接保持一致；无 query / body 时整段省略（GET 无 body）。
/// </summary>
internal static class BitgetSigner
{
    public static string Sign(string apiSecret, string timestamp, string method, string path, string? query, string? body)
    {
        var signatureString = string.Concat(
            timestamp,
            method.ToUpperInvariant(),
            path,
            string.IsNullOrEmpty(query) ? string.Empty : "?" + query,
            body ?? string.Empty);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureString)));
    }
}
