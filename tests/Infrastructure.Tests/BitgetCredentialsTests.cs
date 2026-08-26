using TradingClient.Exchanges.Bitget.Auth;

namespace TradingClient.Infrastructure.Tests;

public class BitgetCredentialsTests
{
    [Fact]
    public void ToString_Always_MasksApiSecretAndPassphrase()
    {
        var credentials = new BitgetCredentials("my-key", "super-secret-value", "my-passphrase");

        var text = credentials.ToString();

        Assert.DoesNotContain("super-secret-value", text);
        Assert.DoesNotContain("my-passphrase", text);
        Assert.Contains("my-key", text);
    }
}
