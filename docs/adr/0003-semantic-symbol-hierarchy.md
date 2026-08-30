# ADR 0003：Symbol 语义化值对象层级

- 状态：已接受
- 日期：2026-08-28

## 背景

交易对、合约与期权符号是交易客户端最基础的领域概念之一。它不仅是 UI 上的显示字符串，还决定了下单精度、产品线分派、保证金计算与交易所格式转换。若用裸字符串表示，不同交易所的格式差异（`BTC_USDT`、`BTCUSDT`、`BTC-250627-100000-C`）会在各层散落解析逻辑；若用弱类型结构，则无法在编译期保证"永续合约没有到期日"这类业务规则。

## 决策

`Symbol` 是抽象 record，由子类型声明所属产品线与语义字段，非法状态在类型层面即无法表示：

- `SpotSymbol(Base, Quote)`：现货。
- `FuturesSymbol` 抽象，`PerpetualFuturesSymbol(Base, Quote)` 与 `DeliveryFuturesSymbol(Base, Quote, Expiry)` 分别表示永续与交割合约；永续类型不携带 `Expiry`。
- `OptionSymbol(Underlying, Expiry, Strike, Right)`：期权四分量模型，为本地期权分析模块预留。

每个交易所适配器实现独立的 `XxxSymbolFormatter`，负责领域 `Symbol` 与交易所原生字符串的双向转换。格式化为适配器职责，不出现在 Domain/Application 层。

## 备选方案与理由

**裸字符串 + 解析函数**：实现最省。否决理由：非法状态可表示（任何字符串都是合法 Symbol），现货/合约/期权无法在编译期区分；解析与格式化逻辑会散落在 UI、用例、适配器各处，新增产品线时必须全量返工。

**单一 record + 产品枚举 + 可空字段**：用一个 record 容纳所有字段，通过 `ProductKind` 枚举区分。否决理由：永续合约会背着无意义的 `Expiry` 字段，且 `ProductKind` 与具体字段的合法组合无法由类型系统保证（例如 `ProductKind.Spot` 时 `Expiry` 应为 null 的约束需在运行时检查）。类型系统本应替我们排除这些非法状态。

## 后果

- 上层代码按 `Symbol.Product` 或模式匹配即可安全分派，无需字符串解析。
- 新增产品线（如期权交易所适配）时，扩展 `Symbol` 子类型即可，不破坏现有代码。
- 每个适配器的符号解析错误被限制在自身程序集内，不影响其他交易所。
