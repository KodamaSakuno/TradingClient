# ADR 0004：接口垂直拆分与 Capabilities 驱动 UI

- 状态：已接受
- 日期：2026-08-28

## 背景

交易所之间的功能差异巨大：有的只支持现货，有的支持合约与双向持仓，有的统一账户不需要内部划转，有的产品线独立分仓。如果上层直接依赖某个交易所的巨型网关，新增交易所时会污染大量调用方；如果 UI 里硬编码交易所名来判断显隐，新增交易所时还得改 UI。

## 决策

Application 层按产品线垂直拆分接口：`ISpotTrading`、`IFuturesTrading`、`IMarketData`、`IAccountService` 等。一个交易所连接器只实现它需要支持的接口子集。上层通过 `IExchangeConnector.Capabilities` 读取能力声明（账户模式、支持的产品线、是否需要内部划转、是否支持双向持仓等），并据此动态显隐 UI 元素。

## 备选方案与理由

**单一巨型网关接口**：把所有功能塞进一个 `IExchangeConnector`，每个交易所都实现全部方法。否决理由：多数方法对特定交易所无意义（例如现货交易所不需要 `SetLeverage`），调用方充斥着空实现或 `NotSupportedException`；新增交易所时改动面扩散到所有消费方。

**UI 里 `if (exchange == "Gate")`**：根据交易所名字硬编码显隐。否决理由：每新增一个交易所都要改 UI，违背开闭原则；能力与交易所名耦合后，同一交易所不同账户模式（Classic/Unified）的能力差异也无法表达。

## 后果

- 新增交易所 = 新增 `Infrastructure/Exchanges.Xxx` 适配器项目 + 契约测试 fixture + 配置文件，不改 Domain/Application 已有代码。
- UI 组件只依赖 `ExchangeCapabilities`，不感知具体交易所名， Classic/Unified 的能力差异自然呈现。
- 接口子集化迫使每个用例只依赖自己需要的最小能力，依赖方向保持向内。
