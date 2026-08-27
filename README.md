# TradingClient — 多交易所交易客户端

基于 Avalonia 的跨平台桌面交易客户端，干净架构（Clean Architecture），支持多交易所、多产品线、多账户模式。

项目定位：生产级架构示范。目标是用最小范围证明架构能力与业务深度——不追求交易所数量，而是让第二个交易所、第二种账户模式尽早接入，用它们检验抽象是否充分。

## 能力矩阵（均为真实环境验证，非 mock）

| 交易所 | 账户模式 | 现货 | 合约（USDT 永续） | 验证方式 |
|---|---|---|---|---|
| Gate | Classic | 行情 / 下单 / 撤单 / 余额 / 私有订单推送 | 下单 / 杠杆 / 持仓 / 双向持仓 / 持仓推送 / 强平预警 | testnet 全链路冒烟通过 |
| Bitget | Unified (UTA) | 行情 / 下单 / 撤单 / 余额 / 私有订单推送 | — | 模拟盘全链路冒烟通过 |

冒烟工具：`tools/GateSmokeTest`（testnet）、`tools/BitgetSmokeTest`（模拟盘），验证签名、时间同步、限流、WS 私有推送的完整真实链路。

## 架构

```
TradingClient.slnx
├── src/
│   ├── Domain/                    # 核心层，零第三方依赖
│   │   ├── Instruments/           # Symbol 语义化层级、Instrument（含完整交易规则）
│   │   ├── Options/               # 期权定价引擎：Black-76 / CRR 二叉树 / BAW / Greeks / IV 反解
│   │   ├── Trading/               # Order、Position、Balance、各类 Update 事件
│   │   └── Primitives/            # Result<T>、ExchangeError、AccountMode、Capabilities
│   ├── Application/               # 用例层，只依赖 Domain
│   │   ├── Abstractions/          # 按产品线垂直拆分的网关接口（无巨型网关）
│   │   ├── Risk/                  # 客户端风控：事前规则链 + 事中 RiskMonitor（见下）
│   │   ├── UseCases/              # Spot / Futures 下单、期权链分析
│   │   └── Services/              # ExchangeRegistry、InstrumentCache
│   ├── Infrastructure/
│   │   ├── Exchanges.Common/      # ExchangeConnectorBase、令牌桶限流、时间同步、WS 传输抽象
│   │   ├── Exchanges.Gate/        # Gate 适配器（防腐层，原生 DTO 不出程序集）
│   │   ├── Exchanges.Bitget/      # Bitget 适配器
│   │   └── Persistence/           # 风控配置 JSON 存储（PostgreSQL 历史库的落点）
│   └── TradingClient.Avalonia/    # UI：MVVM + ReactiveUI + DynamicData，Composition Root 在 App.axaml.cs
└── tests/
    ├── Domain.Tests / Application.Tests / Infrastructure.Tests
    └── Exchanges.ContractTests    # 契约测试：抽象基类 × 各交易所 fixture
```

依赖方向只能向内：`Avalonia → Application → Domain`，`Infrastructure → Application → Domain`。交易所 SDK / REST / WebSocket 只允许出现在适配器项目内。

## 核心设计决策

**Symbol 是语义化值对象，不是字符串。** 现货（Base/Quote）、永续、交割、期权（标的/到期日/行权价/看涨看跌）各有密封子类型，"永续合约没有到期日"这类约束由类型系统保证（非法状态无法表示）。格式化为交易所原生字符串（`BTC_USDT` / `BTCUSDT`）是适配器职责，双向转换。

**按产品线垂直拆分接口。** `ISpotTrading` / `IFuturesTrading` / `IMarketData` / `IAccountService` 各自独立，一个连接器实现其中若干子集。新增交易所 = 新适配器项目 + 契约测试 fixture，不改上层代码。

**能力声明驱动 UI。** 交易所/账户模式/产品线的差异一律经 `ExchangeCapabilities` 表达（账户模式、是否需内部划转、产品线、是否支持双向持仓），UI 不出现硬编码交易所名。

**账户模式归一化。** 上层永远只面对 `AccountSummary`：Classic 模式由适配器把现货/合约钱包聚合，Unified 模式直接映射并保留折算率。口径推导见 [ADR 0001](docs/adr/0001-uta-account-summary-normalization.md)。

**Result 模式。** 业务失败（余额不足、精度错误、交易所拒单）返回 `Result<T>` 携带错误码；异常只用于真正的系统故障。

**数量语义统一。** 领域层只用"币的数量"；Gate 合约的张数↔币换算（quanto multiplier）封装在适配器内。

## 客户端风控（两层）

**事前拦截**：`IPreTradeRiskCheck` 可插拔规则链，下单链路内逐条执行，任一不过即拒单并审计——连接守卫、单笔/单日量限、仓位上限、价格偏离保护、重复下单防护、风控状态闸门（`ReduceOnly` 只放行减仓单且无持仓快照时 fail-closed，`Locked` 全拒）。规则配置持久化（JSON store，接口预留 PostgreSQL）。

**事中熔断**：`IRiskMonitor` 订阅持仓/行情推送持续评估（不挂下单链路）——当日亏损熔断（三档）+ 账户总敞口上限（两档），驱动状态机 `Normal → Warning → ReduceOnly → Locked`；进入 Locked 触发 kill switch 撤销全部未成交单；连接断开同样触发（REST 撤单不依赖 WS）。

**交易所侧兜底**：Gate 期货 `countdown_cancel_all` 死 man's switch——客户端存活时心跳续期，客户端死亡时交易所自动撤单，风控不完全依赖客户端存活。

自成交防护走交易所侧（Gate `stp_act`）。盈亏口径的近似性（realized 基线差分、浮动盈亏本地估算）都在代码注释中写明。

## 测试

xUnit v3（Microsoft Testing Platform runner）。当前 **Domain 82 / Application 114 / Infrastructure 283 / Contract 40，共 519 个，全绿**。

- Domain / Application / Exchanges.Common：纯单元测试，零外部依赖
- 交易所适配器：**契约测试**——针对 `ISpotTrading`/`IFuturesTrading` 等接口的抽象测试基类，每个交易所 fixture（桩 HTTP + 回放 WS）跑同一套用例；新增交易所只需新增 fixture
- 真实链路：`tools/` 下两个冒烟工具对 testnet/模拟盘跑全链路（连接 → 行情 → 余额 → 下单 → 推送 → 撤单/平仓）

## 运行

桌面端（凭证走环境变量，缺失时鉴权接口返回 `MISSING_CREDENTIALS`，其余功能照常）：

```bash
# Gate testnet
export GATE_TESTNET_API_KEY=... GATE_TESTNET_API_SECRET=...
# Bitget 模拟盘
export BITGET_TESTNET_API_KEY=... BITGET_TESTNET_API_SECRET=... BITGET_TESTNET_PASSPHRASE=...
# 部分网络环境 WS/REST 需代理
export GATE_TESTNET_PROXY=http://localhost:7890 BITGET_TESTNET_PROXY=http://localhost:7890

dotnet run --project src/TradingClient.Avalonia
```

冒烟测试：

```bash
dotnet run --project tools/GateSmokeTest                # Gate 现货 testnet
dotnet run --project tools/GateSmokeTest -- --futures   # Gate 合约 testnet
dotnet run --project tools/GateSmokeTest -- --futures --dual  # 双向持仓
dotnet run --project tools/BitgetSmokeTest              # Bitget UTA 现货模拟盘
```

## 路线图

- [x] Gate 现货（Classic）最小闭环 — testnet 真实成交
- [x] Bitget 现货（UTA）+ 契约测试按账户模式参数化
- [x] Gate 合约深入：持仓、杠杆、双向持仓、私有推送、强平预警
- [x] 客户端风控两层（事前规则链 + 事中监控 + kill switch）
- [x] 下单票面板与订单簿 UI
- [x] 期权分析模块：Black-76 定价、Greeks、IV 反解、T 型报价（本地，不接交易所）
- [ ] PostgreSQL 交易历史库与复盘查询（EF Core + Migrations）
- [ ] CI、BenchmarkDotNet 性能基线与调优记录、ADR 补齐

明确排除：Binance 适配器（架构上无新增证明，设计说明留文档）、任何交易所的期权对接（期权能力由本地分析模块展示）。
