namespace TradingClient.Exchanges.Bitget.Models;

/// <summary>
/// GET /api/v3/account/assets 的 data 字段，数值均为字符串（.local/bitget/catalog/uta-account-assets-balance/assets-balance-assets.md）。
/// </summary>
internal sealed record BitgetAccountAssets(
    string AccountEquity,
    string EffEquity,
    string Imr,
    string Mmr,
    string MgnRatio,
    BitgetAccountAsset[] Assets);

internal sealed record BitgetAccountAsset(
    string Coin,
    string Balance,
    string Locked,
    string UsdValue);
