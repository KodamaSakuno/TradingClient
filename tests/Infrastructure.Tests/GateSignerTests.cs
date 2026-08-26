using TradingClient.Exchanges.Gate.Auth;

namespace TradingClient.Infrastructure.Tests;

public class GateSignerTests
{
    // 官方测试向量，出处：.local/gate_api_auth.txt（APIv4 签名示例，key="key" secret="secret" timestamp=1541993715），核对日期 2026-08-26
    [Fact]
    public void Sign_GetWithQueryWithoutBody_MatchesOfficialVector()
    {
        var sign = GateSigner.Sign(
            "secret", "GET", "/api/v4/futures/orders",
            "contract=BTC_USD&status=finished&limit=50", body: null, 1541993715);

        Assert.Equal(
            "55f84ea195d6fe57ce62464daaa7c3c02fa9d1dde954e4c898289c9a2407a3d6fb3faf24deff16790d726b66ac9f74526668b13bd01029199cc4fcc522418b8a",
            sign);
    }

    // 官方测试向量：无 query 时第三段为空串，body 原样参与 SHA512
    [Fact]
    public void Sign_PostWithBodyWithoutQuery_MatchesOfficialVector()
    {
        var sign = GateSigner.Sign(
            "secret", "POST", "/api/v4/futures/orders",
            query: null, """{"contract":"BTC_USD","type":"limit","size":100,"price":6800,"time_in_force":"gtc"}""",
            1541993715);

        Assert.Equal(
            "eae42da914a590ddf727473aff25fc87d50b64783941061f47a3fdb92742541fc4c2c14017581b4199a1418d54471c269c03a38d788d802e2c306c37636389f0",
            sign);
    }

    // 空串的 SHA512 为文档给定常量（gate_api_auth.txt）
    [Fact]
    public void Sign_WithoutBody_UsesEmptyStringSha512Constant()
    {
        const string emptySha512 =
            "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e";

        var withNull = GateSigner.Sign("s", "GET", "/p", null, null, 1);
        var withEmpty = GateSigner.Sign("s", "GET", "/p", null, "", 1);

        Assert.Equal(withEmpty, withNull);
        Assert.Equal(emptySha512, Convert.ToHexStringLower(
            System.Security.Cryptography.SHA512.HashData([])));
    }

    [Fact]
    public void Sign_BodyWhitespaceDiffers_ProducesDifferentSignature()
    {
        var compact = GateSigner.Sign("s", "POST", "/p", null, """{"a":1}""", 1);
        var spaced = GateSigner.Sign("s", "POST", "/p", null, """{ "a": 1 }""", 1);

        Assert.NotEqual(compact, spaced);
    }

    [Fact]
    public void Sign_LowercaseMethod_IsUppercasedBeforeSigning()
    {
        Assert.Equal(
            GateSigner.Sign("s", "GET", "/p", null, null, 1),
            GateSigner.Sign("s", "get", "/p", null, null, 1));
    }

    // WS 签名串为 channel=<channel>&event=<event>&time=<time>（.local/gate_api_spot_ws.txt 鉴权一节）；
    // 期望值用 HMAC-SHA512(secret, 该串) 独立计算核对，2026-08-26
    [Fact]
    public void SignWs_Always_SignsChannelEventTimeString()
    {
        var sign = GateSigner.SignWs("secret", "spot.orders", "subscribe", 1541993715);

        Assert.Equal(
            "e9c13b956612d4af11a7fd2025729fe28979b16153cf00e3aff2e4c7dd0155f413dce9fdff2297d9f62bda58c0463792dab6163597b9095f9600ed0d61990a34",
            sign);
    }

    [Fact]
    public void SignWs_EventDiffers_ProducesDifferentSignature()
    {
        Assert.NotEqual(
            GateSigner.SignWs("s", "spot.orders", "subscribe", 1),
            GateSigner.SignWs("s", "spot.orders", "unsubscribe", 1));
    }
}
