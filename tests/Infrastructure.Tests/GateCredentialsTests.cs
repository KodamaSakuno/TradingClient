using TradingClient.Exchanges.Gate.Auth;

namespace TradingClient.Infrastructure.Tests;

public class GateCredentialsTests
{
    [Fact]
    public void ToString_Always_MasksApiSecret()
    {
        var credentials = new GateCredentials("my-key", "super-secret-value");

        var text = credentials.ToString();

        Assert.DoesNotContain("super-secret-value", text);
        Assert.Contains("my-key", text);
    }
}
