namespace TradingClient.Exchanges.Bitget.Auth;

public sealed record BitgetCredentials(string ApiKey, string ApiSecret, string Passphrase)
{
    // 屏蔽 ApiSecret/Passphrase：record 默认 ToString 会打印全部属性，凭证泄露进日志即违反 §9 凭证安全
    public override string ToString() =>
        $"BitgetCredentials {{ ApiKey = {ApiKey}, ApiSecret = ***, Passphrase = *** }}";
}
