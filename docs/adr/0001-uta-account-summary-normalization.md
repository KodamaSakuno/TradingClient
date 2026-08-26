# ADR 0001：UTA 账户到 AccountSummary 的归一化口径

- 状态：已接受
- 日期：2026-08-26

## 背景

`AccountSummary` 是上层唯一面对的账户模型：普通账户（Classic）与统一账户（Unified）的差异由适配器聚合掉，上层不感知账户模式。Bitget UTA 接入时，`GET /api/v3/account/assets` 返回的字段与 `AccountSummary` 并非一一对应，存在多个合理口径可选。本 ADR 固定 UTA 侧的映射口径，供后续其他交易所的统一账户接入对照。

接口返回（USD 计价）：`accountEquity`（总权益）、`usdtEquity`、`effEquity`（有效权益，可为全仓提供保证金的净值）、`imr`（初始保证金占用）、`mmr`（维持保证金）、`mgnRatio`（维持保证金率）、`assets[]{coin,balance,available,locked,usdValue}`。

## 决策

- `TotalEquity = accountEquity`（USD 口径）
- `AvailableMargin = effEquity − imr`（推导值，非接口直给）
- `InitialMargin = imr`，`MaintenanceMargin = mmr`，`MarginRatio = mgnRatio`（接口直给）
- `AssetBalance`：`Total = balance`，`Frozen = locked`，`EquityValue = usdValue`，`CollateralWeight = null`

## 备选方案与理由

**TotalEquity 取 `usdtEquity` 而非 `accountEquity`**：加密交易习惯以 USDT 计价。否决理由：`imr`/`mmr`/`mgnRatio` 均为 USD 口径，总权益必须与保证金字段同单位，`MarginRatio`（维持保证金 / 总权益，统一账户核心风控指标）的语义才成立；USDT≈USD 的近似不应进入模型层。

**`AvailableMargin` 取资产 `available` 加总**：各币种 available 以本币计价，跨币种加总需逐个折算，等于在适配器里重造 `effEquity`。否决理由：`effEquity` 是交易所算好的可开仓净值，减去已占用 IM 即为可新开仓保证金，口径与交易所强平逻辑一致，且避免折算误差。

**`CollateralWeight` 接 discount-rate 接口填真实折算率**：UTA 的币种折算率在另一接口（`GET /api/v3/market/discount-rate`，见官方文档 catalog trading-risk-rules）。暂缓理由：现货闭环不消费折算率，多一次请求只为填展示字段；模型字段保留，合约阶段做风控展示时再接。

## 后果

- `AvailableMargin` 是推导口径，可能与交易所 UI 展示的"可用"有尾差（如未计挂单冻结以外的抵扣项）；该口径已在 `BitgetConnector.GetAccountAsync` 注释中标注，若实测偏差需回改本 ADR。
- 后续其他交易所的统一账户接入按本 ADR 对照验证 `AccountSummary` 模型的充分性；若出现无法等价的口径，说明领域模型需要修订，应先改模型与本 ADR 再动适配器。
