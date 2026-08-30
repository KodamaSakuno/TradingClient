# AGENTS.md — 多交易所交易客户端

> 本文件是本项目架构的唯一权威来源。任何新增代码、新增交易所适配、新增产品线（现货/合约/期权）都必须遵守本文档的约束。修改架构决策前，先修改本文档并说明理由。

## 1. 项目概述

基于 **Avalonia** 的跨平台桌面交易客户端，采用**干净架构（Clean Architecture）**，支持多交易所、多产品线。

**项目定位：** 生产级架构示范项目。目标是"用最小范围证明最强的架构能力与业务深度"，**不追求交易所数量**。业务场景聚焦在衍生品做市与机构级交易终端：多交易所/账户模式抽象、合约与期权 Greeks 风险展示、客户端风控与低延迟行情路径是核心展示点。

**业务重点：**
- 加密交易所适配用于验证多交易所抽象能力，不作为最终生产目标；
- 期权领域能力通过本地分析模块展示（§12），避免接入与境内商品期权体系差异较大的加密期权；
- 合约深度（§11 阶段 3）对应期货做市与 Delta 对冲场景；
- 做市终端场景（T 型报价、批量改单、一键全撤、Greeks 汇总、风险限额）是产品能力主线。

**路线图（按优先级，已简化）：**

1. Gate 现货（普通账户）—— 最小闭环：行情、下单、余额
2. Bitget 现货（统一账户 UTA）—— 用最低成本同时验证多交易所抽象与账户模式抽象
3. Gate 合约深入 —— 持仓、杠杆、强平、双向持仓、资金费率；业务深度集中在单个交易所做透
4. 客户端风控 + PostgreSQL 交易历史库（见 §6.4、§9.1）
5. 期权分析模块（本地，mock 数据）—— Black-76 定价、希腊字母、T 型报价、波动率微笑（见 §12）
6. 工程化收尾 —— CI、性能基线与调优记录、ADR、README

**排序原则：** 现货跨交易所、合约单交易所深耕。多交易所/账户模式抽象是最大架构风险，用成本最低的现货尽早验证；合约适配器成本约为现货的 3–4 倍，做两家是双倍成本且无新增架构证明，故只深耕 Gate。富余时间的 stretch（按序）：Gate 统一账户现货 → Bitget UTA 合约，均只增加组合象限，不引入新抽象。

**明确排除（设计预留但不实现）：**

- Binance：架构上证明不了新东西，推迟；仅将"如何接入"的设计说明写入 `docs/` 作为架构设计素材
- 期权**交易所适配**（Gate/Binance 期权）：排除——国内商品期权为美式行权、标的为期货（美式定价体系见 §12），与加密期权体系差异大，接加密期权反而偏离境内商品期权业务主线；期权能力改由本地分析模块展示（§12），Symbol 四分量模型的前瞻设计（§4.1）保留

**账户模式：** 交易所同时存在普通账户（Classic）与统一账户（Unified/UTA），两种模式都必须支持，且差异对上层完全透明。

## 2. 技术栈

| 领域 | 选型 | 说明 |
|---|---|---|
| UI 框架 | Avalonia | MVVM 模式 |
| MVVM 库 | ReactiveUI + DynamicData | 高频行情集合（订单簿、持仓）用 DynamicData 管道，禁止手动维护 ObservableCollection |
| 依赖注入 | Microsoft.Extensions.DependencyInjection | Composition Root 在 `App.axaml.cs` |
| 响应式流 | `IObservable<T>`（System.Reactive） | 交易所推送统一转成响应式流 |
| 图表 | LiveCharts2 或 ScottPlot | 两者均支持 Avalonia |
| 日志 | Serilog | 结构化日志，订单全链路带 OrderId 关联 ID |
| 本地持久化 | SQLite + PostgreSQL | SQLite 存运行时缓存与配置；PostgreSQL（Docker）存交易历史与复盘分析，见 §9.1 |
| 性能工具 | BenchmarkDotNet、dotnet-counters、dotnet-trace、PerfView | 关键路径埋点与调优记录，见 §9.2 |
| 测试 | xUnit | 含契约测试，见 §10 |

## 3. 解决方案结构

```
TradingClient.slnx
├── src/
│   ├── Domain/                        # 核心层，零依赖
│   │   ├── Instruments/               # Symbol, Instrument, ProductKind
│   │   ├── Trading/                   # Order, Position, Balance, Trade, 各类 Update 事件
│   │   ├── Options/                   # 期权定价引擎（§12）：Black-76 / CRR 二叉树 / BAW、Greeks、IV 反解
│   │   └── Primitives/                # Result<T>, ExchangeError, AccountMode
│   ├── Application/                   # 用例层，只依赖 Domain
│   │   ├── Abstractions/              # 全部网关接口（§5）
│   │   ├── Risk/                      # 客户端风控（§6.4）：事前规则链 + 事中 RiskMonitor/评估器、限额配置、审计接口
│   │   ├── UseCases/
│   │   │   ├── Spot/                  # PlaceSpotOrder, CancelSpotOrder ...
│   │   │   ├── Futures/               # PlaceFuturesOrder, SetLeverage, ClosePosition ...
│   │   │   ├── Options/               # OptionChainAnalytics（§12）：参数化微笑、T 型报价链路、持仓 Greeks 汇总
│   │   │   ├── MarketData/            # SubscribeInstrument ...
│   │   │   └── Account/               # GetAccountSummary, TransferFunds ...
│   │   └── Services/                  # ExchangeRegistry（管理多个连接器实例）
│   ├── Infrastructure/
│   │   ├── Exchanges.Common/          # ExchangeConnectorBase, 限流器, WebSocket 封装, 时间同步
│   │   ├── Exchanges.Gate/            # 见 §7 适配器内部结构
│   │   ├── Exchanges.Bitget/
│   │   └── Persistence/               # SQLite + PostgreSQL（职责切分见 §9.1）
│   └── TradingClient.Avalonia/
│       ├── ViewModels/
│       │   ├── Spot/
│       │   ├── Futures/
│       │   ├── Options/               # OptionsLab（§12）：T 型报价、波动率微笑、持仓 Greeks 汇总
│       │   └── Shared/                # OrderBook, OrderList, TradeHistory（现货合约复用）
│       ├── Views/
│       └── App.axaml.cs               # Composition Root
└── tests/
    ├── Domain.Tests/
    ├── Application.Tests/
    ├── Infrastructure.Tests/            # Exchanges.Common 等基础设施单元测试
    └── Exchanges.ContractTests/       # 契约测试，见 §10
```

### 分层铁律

- 依赖方向只能向内：`Avalonia → Application → Domain`，`Infrastructure → Application → Domain`。
- **Domain 不允许出现任何交易所 SDK、HTTP 客户端、第三方库引用**（System.Reactive 除外）。
- 交易所 SDK / REST / WebSocket 代码只允许出现在 `Infrastructure/Exchanges.Xxx/` 内。
- 交易所原生 DTO 不允许流出其所在适配器程序集（用 `internal`）。

## 4. 领域模型核心决策

### 4.1 Symbol 是语义化值对象，不是字符串

**禁止**用 `"BTCUSDT"` 这类裸字符串表示交易对。Symbol 保存语义字段，格式化为交易所原生字符串是适配器的职责：

```csharp
public abstract record Symbol(string Raw)
{
    // 产品线不重复存储，由子类型声明
    public abstract ProductKind Product { get; }
}

// 现货：Base + Quote
public sealed record SpotSymbol(string Base, string Quote) : Symbol(...);

// 合约：永续与交割是两个密封子类型，到期日只存在于交割合约（非法状态无法表示）
public abstract record FuturesSymbol(string Base, string Quote, string Raw) : Symbol(Raw)
{
    // 永续/交割同样由子类型声明，不作为构造参数传入
    public abstract ContractKind Kind { get; }
}

public sealed record PerpetualFuturesSymbol(string Base, string Quote)
    : FuturesSymbol(Base, Quote, ...);

public sealed record DeliveryFuturesSymbol(string Base, string Quote, DateOnly Expiry)
    : FuturesSymbol(Base, Quote, ...);

// 期权：标的 + 到期日 + 行权价 + 看涨/看跌
// 四分量模型供 §12 本地期权分析模块使用；交易所期权适配已排除（§1）
public sealed record OptionSymbol(
    string Underlying, DateOnly Expiry, decimal Strike, OptionRight Right) : Symbol(...);
```

每个适配器实现自己的 `XxxSymbolFormatter`，负责双向转换：
- 领域 Symbol → 交易所字符串（Gate：`BTC_USDT`；Bitget/Binance：`BTCUSDT`；期权：`BTC-250627-100000-C` 各家格式不同）
- 交易所字符串 → 领域 Symbol（拉取 instruments 时的反向解析）

**设计意图：** 期权符号含四个语义分量，若初期用裸字符串，接入期权时必然返工。

### 4.2 Instrument 携带完整交易规则

```csharp
public sealed record Instrument(
    Symbol Symbol,
    decimal TickSize,             // 最小价格变动
    decimal StepSize,             // 最小数量变动
    decimal MinQuantity,
    decimal? MinQuoteAmount,      // 最小名义金额，null 表示无限制
    decimal? ContractMultiplier,  // 合约/期权乘数，现货为 null
    InstrumentStatus Status)
{
    // 产品线不重复存储，由 Symbol 子类型决定（Symbol.Product 为抽象属性，子类必须声明）
    public ProductKind Product => Symbol.Product;
}
```

下单前的价格对齐 tick、数量对齐 step 等校验**统一在 Domain/Application 层基于 Instrument 完成**（`AlignPrice` / `AlignQuantity` / `ValidateOrder`），适配器不重复实现。

### 4.3 账户模型归一化（模式无关）

上层永远只面对 `AccountSummary`，普通/统一账户的差异由适配器聚合掉：

```csharp
public sealed record AccountSummary(
    AccountMode Mode,                 // Classic / Unified
    decimal TotalEquity,              // Unified=总权益；Classic=各子账户折算合计
    decimal AvailableMargin,
    decimal InitialMargin,            // 已占用 IM
    decimal MaintenanceMargin,
    decimal MarginRatio,              // MM / Equity，统一账户核心风控指标
    IReadOnlyList<AssetBalance> Assets);

public sealed record AssetBalance(
    string Asset, decimal Total, decimal Frozen,
    decimal? CollateralWeight,        // 统一账户折算率，普通账户为 null
    decimal EquityValue);             // 折算后计价币价值

public enum AccountMode { Classic, Unified }
```

- Classic 模式：适配器把"现货钱包 + 合约钱包"聚合成 AccountSummary。
- Unified 模式：直接映射交易所返回，保留折算率信息。
- 强平风险口径：Classic 按仓位独立；Unified 是**组合级**，以账户级 MarginRatio 为准。

## 5. Application 层核心抽象

### 5.1 按产品线垂直拆分接口

**禁止**把所有功能塞进单一巨型网关。一个交易所连接器实现下列接口的若干个子集：

```csharp
public interface IExchangeConnector          // 公共：连接、鉴权、心跳、能力声明
{
    string ExchangeId { get; }
    ExchangeCapabilities Capabilities { get; }
    Task ConnectAsync(CancellationToken ct);
    IObservable<ConnectionState> ConnectionStates { get; }
}

public interface ISpotTrading : IExchangeConnector
{
    Task<Result<SpotOrder>> PlaceSpotOrderAsync(PlaceSpotOrderRequest req, CancellationToken ct);
    Task<Result> CancelSpotOrderAsync(Symbol symbol, string orderId, CancellationToken ct);
    IObservable<SpotOrderUpdate> SpotOrderUpdates { get; }
}

public interface IFuturesTrading : IExchangeConnector
{
    Task<Result<FuturesOrder>> PlaceFuturesOrderAsync(PlaceFuturesOrderRequest req, CancellationToken ct);
    Task<Result> SetLeverageAsync(Symbol symbol, int leverage, MarginMode mode, CancellationToken ct);
    Task<Result<IReadOnlyList<Position>>> GetPositionsAsync(CancellationToken ct);
    IObservable<PositionUpdate> PositionUpdates { get; }
    IObservable<LiquidationWarning> LiquidationWarnings { get; }
}

public interface IMarketData : IExchangeConnector
{
    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(ProductKind product, CancellationToken ct);
    IObservable<Quote> SubscribeQuotes(Symbol symbol);
    IObservable<Trade> SubscribeTrades(Symbol symbol);
    IObservable<OrderBookDelta> SubscribeOrderBook(Symbol symbol);
    IObservable<Candle> SubscribeCandles(Symbol symbol, TimeFrame tf);
}

public interface IAccountService : IExchangeConnector
{
    Task<Result<AccountSummary>> GetAccountAsync(CancellationToken ct);
    Task<Result> TransferFundsAsync(TransferRequest req, CancellationToken ct); // 见 §6.2
}

// 远期预留：现在只定义接口，不实现
public interface IOptionsTrading : IExchangeConnector { ... }
```

### 5.2 能力声明驱动 UI

各交易所/账户模式/产品线的功能差异，**一律通过 Capabilities 表达**，UI 据此动态显隐，禁止在 UI 里写死 `if (exchange == "Gate")`：

```csharp
public record ExchangeCapabilities(
    AccountMode AccountMode,
    bool RequiresInternalTransfers,       // Classic=true, Unified=false；Binance 期权独立账户=true
    IReadOnlyList<ProductKind> Products);
```

目标形态还包括 `SupportsDualPositionMode`（双向持仓）、`MarginModes`（Cross / Isolated，PortfolioMargin 预留枚举值）、`OrderTypes`；均为合约阶段（阶段 3 起）才需要的能力，骨架阶段不保留。

## 6. 账户模式（Classic / Unified）处理规范

### 6.1 连接器工厂 + 模式探测

连接时**先探测账户模式，再实例化对应连接器**，对上层完全透明：

```csharp
public sealed class BitgetConnectorFactory
{
    public async Task<IExchangeConnector> CreateAsync(Credentials cred, CancellationToken ct)
    {
        var mode = await DetectAccountModeAsync(cred, ct);  // 用只读接口探测
        return mode == AccountMode.Unified
            ? new BitgetUtaConnector(cred)      // V3 API
            : new BitgetClassicConnector(cred); // V2 API
    }
}
```

- 即使某个交易所两种模式 API 差异小，也默认走工厂方案（模式差异只会随时间增多）。
- **每次重连重新探测模式**；发现模式变化则重建连接器并刷新 UI 能力面。
- Gate 的两种模式若共用接口，允许在连接器内部用 `IAccountModeStrategy` 策略分支代替双实现。

### 6.2 资金划转是条件能力

- Classic 模式需要账户间划转（现货 ↔ 合约），Unified 不需要。
- `TransferFundsAsync` 用例保留，但 UI 根据 `Capabilities.RequiresInternalTransfers` 决定显隐。
- 注意：Binance 期权是独立期权账户，即便其现货/合约已接入，对期权产品该能力仍为 true。

### 6.3 下单前校验按模式分支

抽 `IPreTradeValidator`，每种账户模式一个实现，由连接器提供：

- Classic 合约单：校验"合约账户可用余额 ≥ 开仓所需"。
- Unified 单：校验"可用保证金 + 本单对组合 IM 的增量"。

### 6.4 客户端风控

风控分两层：**事前拦截（下单链路内）+ 事中熔断（独立监控流）**。对于资金体量大的做市商，风控重心在账户级持续限额，单笔校验只是卫生设施。

**事前：`IPreTradeRiskCheck` 下单前风控链**（Application 层，独立于账户模式，在 `IPreTradeValidator` 之前执行，任一规则不通过即拒单并返回原因）：

- 单笔 / 单日下单量与仓位上限（按 Symbol 可配置）
- 价格偏离保护：限价单价格偏离最新价超阈值时拒绝（"二次确认"是 UI 概念，规则本身只拒）
- 重复下单防护：拦截短时间内相同 Symbol + 方向 + 价格区间的重复提交
- 断线拒单：连接断开时拒单（`ConnectionGuardRule`）
- 风控状态闸门：读事中监控的状态机，`ReduceOnly` 状态仅允许减仓单（依持仓快照推算，无显式 reduce-only 标志），`Locked` 状态全部拒单
- 自成交防护走交易所侧：Gate 用下单 `stp_act`（cn/co/cb）参数；客户端不做自成交拦截（需在途订单跟踪，成本与收益不成比例）

**事中：`IRiskMonitor` 持续风险监控**（订阅持仓/盈亏推送流持续评估，不挂在下单链路上）：

- 敞口上限：单 Symbol 净持仓上限、账户总敞口（notional）上限，超限进入 `Warning` / `ReduceOnly`
- 当日盈亏熔断：当日已实现 + 浮动亏损达阈值 → `Warning` → `ReduceOnly` → 继续恶化触发 kill switch（撤销全部未成交单，可选自动减仓）并进入 `Locked`。浮动盈亏用 entry_price vs 最新价本地估算，口径注释写明，不假装精确
- 风控状态机：`Normal → Warning → ReduceOnly → Locked`，由 `IRiskMonitor` 写、事前链的闸门规则读；每次状态变更广播 UI 显著告警 + 写审计日志
- 断线 kill switch：客户端连接断开时触发撤单；同时以交易所侧死 man's switch（Gate 期货 `POST /futures/{settle}/countdown_cancel_all`，客户端存活时心跳续期）做兜底——风控不完全依赖客户端存活

**实现要求：** 规则可插拔（规则列表注入、逐条执行），新增规则不改调用方；规则配置持久化；每次拦截与状态变更写审计日志（含规则名与原因）。

**演示范围：** 事中监控先实现"当日亏损熔断 + 总敞口上限"两条评估器；希腊字母限额等做市深层规则留扩展点，与 §12 期权模块衔接（Greeks 限额是 `IRiskMonitor` 的新评估器，不动框架）。本模块是"风险意识"的核心展示项。

## 7. 交易所适配器规范（防腐层）

每个交易所一个独立项目，内部结构：

```
Exchanges.Bitget/
├── BitgetConnectorFactory.cs      # 模式探测 + 连接器选择
├── BitgetClassicConnector.cs      # 普通账户（实现 §5 接口子集）
├── BitgetUtaConnector.cs          # 统一账户
├── BitgetSymbolFormatter.cs       # 双向符号转换
├── BitgetPreTradeValidator.cs
├── Auth/                          # 签名、时间戳（内部维护与服务器的时间偏移）
└── Models/                        # 原生 DTO，全部 internal
```

适配器必须封装的差异（**均不得外泄到 Application/Domain**）：

- REST/WebSocket 协议、认证签名、频道订阅协议
- 限频：适配器内置令牌桶限流器，宁保守勿激进
- 断线重连：指数退避，由 `Exchanges.Common` 的 `ExchangeConnectorBase` 提供
- 时间同步：维护与服务器的时间偏移，避免签名时间戳被拒
- 持仓模式协商：交易所单向/双向持仓模式不同，领域层 `Position` 固定带 `PositionSide`（Long/Short/Both），适配器负责映射
- 数量语义：领域层统一用"币的数量"，张数/币本位的换算在适配器内完成

公共逻辑（重连退避、限流器骨架、WebSocket 封装）沉淀到 `Exchanges.Common` 的 `ExchangeConnectorBase`。

## 8. UI 层（Avalonia）规范

### 8.1 线程与性能

- 交易所推送在后台线程，**必须在 ViewModel 边界统一切回 UI 线程**（`ObserveOn(RxApp.MainThreadScheduler)`），适配器不得触碰 UI 线程。
- 行情流在 Application 层做节流（`Sample`/`Throttle`，100–200ms 合并一次），禁止全量 tick 直刷 UI。
- 订单簿用增量更新（`OrderBookDelta`），禁止整表重建。

### 8.2 组件复用

- 现货/合约**共享**：OrderBook、K线、成交记录、订单列表 → 放 `ViewModels/Shared/`。
- 合约**特有**：杠杆滑块、保证金模式切换、持仓面板（未实现盈亏、资金费率倒计时）→ 放 `ViewModels/Futures/`。
- 顶部"交易所 + 产品类型"选择器（如 `Bitget · 合约`），切换即更换当前活动连接器实例（由 `ExchangeRegistry` 提供）。

### 8.3 账户面板按模式渲染

- Classic：显示"现货钱包 / 合约钱包"分栏 + 划转按钮。
- Unified：显示"权益 / 可用保证金 / 保证金占用率"进度条；MarginRatio 接近危险阈值时给出显眼告警。
- 统一账户下**不显示单仓位强平价**（组合级强平，该概念不成立），改为账户级风险缓冲展示。

### 8.4 依赖方向

ViewModel 只依赖 Application 层用例与接口，**禁止直接 new 连接器**。全部对象图在 `App.axaml.cs` 的 Composition Root 组装，交易所启用列表走配置文件。

## 9. 错误处理与横切

- **Result 模式**：业务失败（余额不足、精度错误、交易所拒单）返回 `Result<T>` 携带错误码，**禁止用异常传递业务错误**；异常只用于真正的系统故障。
- **配置驱动**：`appsettings.json` 配置启用的交易所与凭证引用，新增交易所 = 新增适配器项目 + 一行配置，不改现有代码。
- **日志**：Serilog 结构化输出；订单从发起到终态（成交/撤销/拒绝）全链路带 `OrderId` 关联 ID。
- **凭证安全**：API Key 不进代码库、不进日志；本地存储需加密。

### 9.1 PostgreSQL 交易历史库

- 职责切分：SQLite 管运行时缓存与配置（桌面客户端的合理选择）；PostgreSQL（Docker）管成交/订单历史、风控审计日志、复盘分析查询（非实时路径）。
- Schema 要求：范式化设计；在 `(symbol, ts)`、`(order_id)` 上建索引；至少一个需要 Join 的复盘查询（如按标的汇总盈亏），并用 `EXPLAIN ANALYZE` 验证索引命中。
- 访问方式：EF Core（Npgsql provider）常规访问 + Migrations 管理迁移；性能敏感查询允许手写 SQL。
- **选型说明：** 选用 PostgreSQL 的理由：开源、轻量、Linux 原生、Docker 本地起库成本低；规范化设计、索引、Join、执行计划分析等核心技能在各主流关系型数据库间可迁移。持久化经 EF Core provider 隔离，切换到其他关系型数据库（如 SQL Server）仅需更换 provider 与连接串——此隔离本身即架构展示点，应在架构说明中主动写明该决策与迁移成本。

### 9.2 性能分析与调优规范

- 关键路径埋点：行情推送到 UI 渲染的端到端延迟、订单簿增量更新吞吐、下单往返耗时。
- 基线：热路径（订单簿应用 delta、符号解析、限流器）用 BenchmarkDotNet 出基线数据，摘要写入 README。
- 调优记录：每次优化前后各留一份 dotnet-counters / dotnet-trace / PerfView 数据，连同"发现—分析—优化—验证"的结论存入 `docs/perf/`，形成可追溯的调优闭环。

## 10. 测试策略

| 层 | 测试方式 |
|---|---|
| Domain / Application / Exchanges.Common | 纯单元测试，无任何外部依赖 |
| 交易所适配器 | **契约测试**：针对 `ISpotTrading`/`IFuturesTrading` 等接口写抽象测试基类，每个交易所实现跑同一套用例 |
| 集成测试 | 用录制的回放数据，禁止依赖真实网络 |

契约测试基类示例（新增交易所时只需新增 fixture）：

```csharp
public abstract class FuturesTradingContractTests
{
    protected abstract IFuturesTrading CreateConnector();  // mock/回放数据
    [Fact] public Task PlaceOrder_WithInvalidPrecision_ReturnsValidationError();
    [Fact] public Task SymbolRoundtrip_PreservesSemantics();
    [Fact] public Task PositionUpdates_CarryPositionSide();
}
```

**测试方法命名约定：** `Method_Scenario_Expectation`，段内 PascalCase、段间下划线（如 `PlaceSpotOrderAsync_WithInvalidQuantity_ReturnsValidationError`）；类名 PascalCase。测试框架用 xUnit v3（Microsoft Testing Platform runner，见仓库根 `global.json`）。

**契约测试须按账户模式参数化**：同一套用例分别在 Classic / Unified 的 mock 数据下运行，重点覆盖 AccountSummary 聚合与 Capabilities 声明的正确性。

## 11. 实施顺序（阶段验收标准）

| 阶段 | 内容 | 验收 |
|---|---|---|
| 1 | Domain + Application 抽象；Gate 现货（普通账户）最小闭环：行情、下单、余额；UI 骨架 | 真实完成一笔小额现货单 |
| 2 | Bitget 现货（UTA）；契约测试建立（两种账户模式参数化） | 两个交易所同一套契约测试全绿，上层零改动 |
| 3 | Gate 合约深入：持仓、杠杆、强平推送、双向持仓、资金费率 | 合约全链路在测试网闭环；契约测试扩展至期货接口 |
| 4 | 客户端风控链（§6.4）+ PostgreSQL 交易历史库与复盘查询（§9.1） | 风控拦截有审计记录；复盘查询 `EXPLAIN ANALYZE` 验证索引命中 |
| 5 | 期权分析模块（§12）：定价、Greeks、IV 反解、T 型报价 UI、波动率微笑 | 定价/Greeks 数值对照解析解的单元测试通过；T 型报价可交互 |
| 6 | 工程化收尾：CI、性能基线与调优记录（§9.2）、ADR 补齐、README 打磨 | CI 全绿；README 含架构图、性能数字、决策摘要 |

**架构检验标准：** 第二个交易所、第二种账户模式尽早接入——它们是检验抽象是否充分的唯一标准。阶段 2 即覆盖"Gate 普通账户 + Bitget 统一账户"两个最有代表性的象限，是项目的第一个关键里程碑。

**推迟项（仅设计说明，不实现）：** Binance 接入要点（限频更严、代理需求、现货/合约 WebSocket 分离）写入 `docs/`；期权仅保留 §4.1 的 Symbol 前瞻设计与 §12 的本地分析模块，**不接任何交易所期权**。

## 12. 期权分析模块（本地，不接交易所）

**定位：** 本项目聚焦场内衍生品做市商场景（期货 + 期权），期权做市是核心业务能力；本模块展示期权领域理解与桌面端功力。**不做任何交易所期权适配。**

- 定价引擎放 Domain 层（纯函数、零依赖、易测试）
- 国内商品期权为**美式行权**（大商所/郑商所均为美式），美式定价是主力而非增强：
  - **CRR 二叉树**：美式定价基准实现——提前行权逐节点处理，收敛到真实美式价格；欧式场景必须收敛到 Black-76（免费的一致性测试）
  - **BAW（Barone-Adesi-Whaley 近似）**：快速近似，供 IV 反解与 T 型报价批量定价等高频调用；与二叉树的差值即近似误差展示点
  - **Black-76**：欧式基准保留——BAW 结构依赖它；"美式价 − 欧式价 = 提前行权溢价"本身是展示点。期货期权美式 call/put 两侧都可能提前行权（行权即拿内在价值吃利息），与无分红股票美式 call 不同
- 希腊字母 Delta/Gamma/Vega/Theta/Rho：Black-76 用解析解；美式模型用 bump-and-revalue 数值 Greeks（Vega 按 1 个波动率百分点、Theta 按自然日、Rho 按 1 个利率百分点，约定在代码注释钉死）
- 隐含波动率反解：Newton 迭代 + 二分法兜底；处理不收敛与无套利边界（价格低于内在价值时返回明确错误而非 NaN）；基于美式价格反解
- UI：T 型报价表（行权价 × 看涨/看跌，做市终端标志性界面）、波动率微笑曲线、持仓 Greeks 汇总面板
- 数据：mock / 手工录入标的行情；模型参数（无风险利率、波动率曲面）走配置文件
- 测试：定价与 Greeks 对照已知解析解做数值校验单元测试；IV 反解做往返一致性测试

## 13. 给编码代理的工作规则

1. 新代码必须先落入正确的层（§3），拿不准时读接口定义，不要发明新的依赖方向。
2. 新增交易所 = 新增 `Infrastructure/Exchanges.Xxx` 项目 + 配置项 + 契约测试 fixture，**不允许修改 Domain/Application 已有代码**（除非发现抽象缺陷，此时必须先更新本文档）。
3. 任何"某交易所特殊"的逻辑，默认属于适配器；想往上层放之前，先检查能否用 Capabilities 表达。
4. UI 功能显隐一律读 `ExchangeCapabilities`，禁止硬编码交易所名。
5. 跨层传递的模型必须是 Domain 类型；交易所原生 DTO 出适配器程序集即为违规。
6. 提交前：契约测试全绿，且两种账户模式的参数化用例都覆盖到新功能。
7. 重大架构决策必须在 `docs/adr/` 留 ADR（背景、备选方案、选择理由），编号递增。
8. 热路径改动必须更新 BenchmarkDotNet 基线；性能优化必须有前后对比数据并存入 `docs/perf/`。
9. 不要实现 Binance 适配器与任何交易所期权对接（§1 已列为排除项），相关改动仅限文档与设计说明；本地期权分析模块（§12）不在此限。
10. 提交信息遵守 §14 的规范。
11. 注释只保留代码无法表达的信息，删除复述代码行为的注释。值得写的：约束与禁令（如"禁止 double 转换以免引入浮点误差"）、外部系统的怪癖（如 Gate 数值字段以字符串返回）、决策依据（直接写明理由；复杂决策已在 `docs/adr/` 留记录的，可引用 ADR 编号如 `ADR-0002`）、录制/fixture 数据的出处与日期。行为变化时同步更新或删除过期注释。代码注释用中文；提交信息等面向 git 历史的文本按 §14 用英文。
12. **代码注释中禁止引用本文件（AGENTS.md）或其章节号。** AGENTS.md 是编码代理的工作约束，会随流程演进而重构，不是代码的文档依赖；注释需要指向架构决策时，一律引用 `docs/adr/` 下的 ADR 编号（规则 7、11）。

## 14. 提交信息规范

采用 Conventional Commits 的简化版，不引入 commitlint 等强制工具：

```
<type>(<scope>): <subject>
```

- **type**（只用这 6 个）：`feat`、`fix`、`refactor`、`test`、`docs`、`chore`（构建/CI/依赖变更归 `chore`）
- **scope**（取自解决方案结构，保持有界）：`domain`、`application`、`gate`、`bitget`、`common`、`ui`、`persistence`、`tests`、`repo`；跨层改动用 `repo` 或省略 scope
- **subject**：英文、祈使句、首字母小写、不加句号、≤ 72 字符

示例：

```
feat(domain): introduce semantic Symbol hierarchy
feat(application): define per-product gateway interfaces
refactor(domain): split FuturesSymbol into perpetual and delivery subtypes
test(contract): parameterize futures contract tests by account mode
chore(repo): add central package management
docs(adr): record account mode factory decision
```

- 平常一行即可；涉及架构权衡时必须写 body（动机 + 备选方案），与 `docs/adr/` 互补
- 全仓库统一用英文 subject，禁止中英混用
