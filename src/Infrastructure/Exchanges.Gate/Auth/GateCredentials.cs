namespace TradingClient.Exchanges.Gate.Auth;

public sealed record GateCredentials(string ApiKey, string ApiSecret)
{
    // 屏蔽 ApiSecret：record 默认 ToString 会打印全部属性，凭证泄露进日志即违反 §9 凭证安全
    public override string ToString() => $"GateCredentials {{ ApiKey = {ApiKey}, ApiSecret = *** }}";
}
