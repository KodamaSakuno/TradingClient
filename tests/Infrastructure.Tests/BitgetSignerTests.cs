using TradingClient.Exchanges.Bitget.Auth;

namespace TradingClient.Infrastructure.Tests;

public class BitgetSignerTests
{
    // KAT 期望值生成方式：python hmac.new(secret, ts+method+path[+?query][+body], sha256) → base64，核对日期 2026-08-26
    [Fact]
    public void Sign_GetWithoutQueryAndBody_MatchesPrecomputedVector()
    {
        var sign = BitgetSigner.Sign(
            "test-secret", "1627292109612", "GET", "/api/v3/account/assets",
            query: null, body: null);

        Assert.Equal("OAfq/XwiGcVlj6bSFDfcIEwngqfuwe7aEgXbs+Hj0t0=", sign);
    }

    // query 非空时拼 "?" + queryString 参与签名
    [Fact]
    public void Sign_GetWithQuery_MatchesPrecomputedVector()
    {
        var sign = BitgetSigner.Sign(
            "test-secret", "1627292109612", "GET", "/api/v3/market/instruments",
            "category=SPOT", body: null);

        Assert.Equal("GWZRMIrRaphNYDZepFEyG4T7rOP6r0s31xsjZNXokYY=", sign);
    }

    [Fact]
    public void Sign_PostWithBody_MatchesPrecomputedVector()
    {
        var sign = BitgetSigner.Sign(
            "test-secret", "1627292109612", "POST", "/api/v3/trade/place-order",
            query: null, """{"symbol":"BTCUSDT","side":"buy"}""");

        Assert.Equal("QgDKYPNgN26OycTn+R5i39ZuOUW/NPYjCN0G74vc8fs=", sign);
    }

    // 规范要求 queryString 为空时整段省略：空串与 null 不得拼出 "?"
    [Fact]
    public void Sign_EmptyQuery_IsSameAsNullQuery()
    {
        Assert.Equal(
            BitgetSigner.Sign("s", "1", "GET", "/p", null, null),
            BitgetSigner.Sign("s", "1", "GET", "/p", "", null));
    }

    [Fact]
    public void Sign_NullBody_IsSameAsEmptyBody()
    {
        Assert.Equal(
            BitgetSigner.Sign("s", "1", "POST", "/p", null, null),
            BitgetSigner.Sign("s", "1", "POST", "/p", null, ""));
    }

    [Fact]
    public void Sign_LowercaseMethod_IsUppercasedBeforeSigning()
    {
        Assert.Equal(
            BitgetSigner.Sign("s", "1", "GET", "/p", null, null),
            BitgetSigner.Sign("s", "1", "get", "/p", null, null));
    }
}
