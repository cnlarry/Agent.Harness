# Microsoft Agent Framework 可行性与 Agent Harness 蓝图

> 2026-10-23：本文自 EOS 的 `docs/plans/` 迁入 Agent Harness 仓库；原代号 AgentKit 统一改为 Agent Harness，包/命名空间为 `Agent.Harness.*`。

> 版本：v1（2026-10-23）
> 范围：EOS 工作助手现状复盘 + Microsoft Agent Framework（下称 MAF）全面使用可行性 + 面向多个企业管理系统复用的 Agent Harness 蓝图
> 状态：设计蓝图，随 Agent Harness 仓库跟踪（2026-10-23 自 EOS 本机文档迁入）
> 实现状态（2026-10-27）：0.1.0 骨架已落，`dotnet build` 0 警告 0 错误，`dotnet test` 106/106；P0 spike 10/10（`deepseek-flash`）。**EOS 已把 `MafAgentEngine` 作为唯一引擎接入**（权限、写管线、预算、审计仍在宿主侧），跨引擎 parity 对拍已随旧引擎退役。下文涉及 parity 与灰度的段落是当时的计划记录，现状以上述为准。
> 结论先行：**当时没选 MAF，不是评估后否决，而是它根本没进入候选名单；今天"全面使用"在技术上可行，但不建议把 EOS 助手整体重写。推荐"MAF 做引擎、Agent Harness 做治理壳、EOS 做第一个宿主"的分层路线。**

---

## 0. 一页结论

| 问题 | 结论 |
|---|---|
| 当时为什么没选 MAF？ | 仓库里没有任何 MAF / Semantic Kernel / AutoGen 的评估记录。真实决策是 ADR-007 的"要不要独立编排器"，答案是"不需要"；一旦按单进程 `ChatService` 落地，以编排见长的 MAF 就没有入口了。 |
| 当时的选择合理吗？ | 对"只做一个受控的单 Agent 工具循环"是合理的：MAF 不提供 ERP 权限、字段过滤、写管线、审批卡、成本台账、审计身份；这些才是当时的主要工作量。 |
| 今天全面使用可行吗？ | **核心 Agent 循环、工具、中间件、工作流可行**；Foundry Hosted、Durable Task、A2A、Harness 的文件/Shell 能力不适合当前本地 IIS + SQL Server 的部署形态。 |
| 值得全量重写吗？ | 不值得。MAF 能替换的核心循环约 **900–1,300 行**；助手特性总共 **17,157 行 / 106 个文件**，其余约 90% 是工具、语义层、诊断、配置面、治理与审计，换框架不会减少。 |
| 推荐路线 | **方案 C：MAF 做引擎 + Agent Harness 做壳 + EOS 渐进迁移**。先读路径灰度，再写路径；保留一键回滚。 |
| 时间估算 | EOS 助手迁移到行为等价约 **6–8 周**（单人全职）；抽出可复用 Agent Harness 再 **3–6 周**（取决于是否有第二个宿主）；工作流试点 **3–4 周/场景**。 |
| 成功标准 | SSE 契约不变、31 个工具行为不变、越权 0 泄露、成本与延迟不退化、可一键切回旧引擎。 |
| 不做什么 | 不换数据库、不换前端、不引入 Azure 依赖、不做多智能体平台、不重写 31 个工具、不在生产用 Harness 的 Shell/文件能力。 |

**我的建议（一句话）：先花 5 天做 P0 spike；通过后按 P1 → P2 → P3 把 EOS 助手切到 MAF 引擎，但保留 Agent Harness 治理壳；P4 再抽包。不要全量重写，也不要用 Hosting / Durable / Harness。**

**怎么读：**

- 只想看结论：§0 + §4.8；
- 想知道"为什么当时没选"：§2；
- 想判断"值不值得"：§4；
- 想照着落地：§5–§7；
- 想验收/回滚：§8。

**一句话回答"为什么当时没帮你选 MAF"：**

> 因为当时的需求不是"搭一个 Agent 框架"，而是"在现有 ERP 里加一个受控的聊天工具循环"。AI 编程工具按 ADR-007 的约束选了最小心智负担的方案；它优化的是"尽快可用 + 不引入新依赖"，不是"跨项目复用"。这个选择在当时是合理的，但它确实留下了一个复用缺口——现在补，正合适。

---

## 1. 事实基线：EOS 工作助手现在是什么

### 1.1 时间线（关键事实）

| 日期 | 事件 | 意义 |
|---|---|---|
| 2026-04-02 | MAF **1.0.0 GA** 发布 | 助手开工前 4 个多月，MAF 已可用 |
| 2026-08-07 | AI 试点评估集基线 38/38（100%） | 当时的评估只覆盖旧链路 |
| 2026-08-25 | ADR-007 拍板；助手 M2–M4 当天落地 | `IChatModel` + `ChatService` + 工具循环 + SSE 抽屉 |
| 2026-09-30 | ADR-016 结题 | 处境、诊断、动作层、配置面成型 |
| 2026-10-23 | 助手 31 个工具；MAF 最新稳定版 1.25.0 | 本文的分析时点 |

> MAF 1.0.0 的发布日期来自 NuGet 包注册信息（published 2026-04-02）；1.25.0 为截至本文时点的最新稳定版。参见 [NuGet: Microsoft.Agents.AI](https://www.nuget.org/packages/Microsoft.Agents.AI)。

### 1.2 代码规模：核心循环只是冰山一角

按 `EOS.API/Features/Assistant` 统计（`*.cs`，含注释与空行）：

| 区域 | 文件数 | 行数 | 与 MAF 的关系 |
|---|---:|---:|---|
| `Tools/` | 35 | 3,929 | 31 个工具的契约与实现；**不换**，只加适配 |
| `Parameters/` | 10 | 2,197 | 参数目录、作用域、解析；**不换** |
| `ModelAccess/` | 12 | 1,783 | 供应商目录、密钥、运行期切换、OpenAI 兼容流；**部分换** |
| `Config/` | 8 | 1,647 | 配置诊断/克隆/预演/写入；**不换** |
| `Actions/` | 9 | 1,492 | 动作门禁、幂等、审批请求；**不换** |
| `Diagnosis/` | 6 | 1,423 | 对象级诊断；**不换** |
| `Situation/` | 6 | 1,126 | 处境感知；**不换** |
| `ChatService.cs` | 1 | 1,066 | 会话编排 + 工具循环 + 落库 + 治理；**部分换** |
| `Metrics/` | 7 | 953 | 指标语义层；**不换** |
| `Memory/` | 2 | 575 | 记忆与提炼；**不换** |
| `Catalog/` | 2 | 350 | 机制自描述；**不换** |
| `Kb/` | 4 | 244 | 知识库摄入/校验；**不换** |
| `Admin/` | 2 | 240 | 变更集/字段元数据；**不换** |
| `Governance/` | 2 | 235 | 成本上限与熔断；**不换** |
| **合计** | **106** | **17,157** | |

其中 `ChatService.StreamReplyAsync` 的主循环约 **420 行**（从第 232 行到第 655 行），加上 `OpenAiCompatibleChatModel` 的流式协议解析约 **300 行**、`AssistantToolRegistry` 的声明/过滤约 **185 行**，MAF 真正能替换的"引擎部分"约 **900–1,300 行**，占全特性的 **5%–8%**。

**这不是说 MAF 没价值，而是说它的价值不在"删掉多少行"，而在"把这几百行变成一个跨项目可复用的标准引擎"。**

### 1.3 四条不可逾越的边界（换引擎也不能动）

| 边界 | 含义 | MAF 能否替代 |
|---|---|---|
| 模型推理内聚在 `EOS.API` | 前端不持密钥、不直连厂商 | 不替代；MAF 只替换进程内循环 |
| 模型输出永不进入 SQL/表达式解析器 | 助手不能成为绕过白名单的后门 | 不替代；仍是 EOS 工具层职责 |
| 业务数据读取只能由 `EOS.API` 授权提供 | 权限与普通请求同一套 | 不替代；MAF 不是权限系统 |
| 助手不得绕过 API 直连数据库 | 与所有调用者同等约束 | 不替代；工具实现仍走仓储 |

这四条来自 [ADR-007](../decisions/ADR-007-EOS.Client下线与工作助手架构收敛.md) 第 5 节，是本次分析的前提。任何"全面使用 MAF"的方案，如果削弱这四条，直接否掉。

### 1.4 它其实已经是一个"垂直 Agent 框架"

现有助手已经具备很多 Agent 框架该有的能力，只是没有把它们抽成通用件：

| 能力 | EOS 现有实现 | MAF 对应 |
|---|---|---|
| 多轮工具调用 | `ChatService` 的 `for round <= MaxToolRounds` | `FunctionInvokingChatClient` |
| 工具白名单 + JSON Schema | `AssistantToolRegistry` + `ToolDefinition` | `AITool` / `AIFunction` |
| 只读工具并行 | `CanRunInParallel` + `Task.WhenAny` | `AllowConcurrentInvocation`（需自定义风险策略） |
| 流式事件 | `ChatStreamEvent` + SSE | `AgentResponseUpdate` |
| 会话持久化 | `ASSISTANT_SESSION/MESSAGE` | `AgentSession` / `ChatHistoryProvider` |
| 上下文裁剪 | 按模型窗口 + 字符类别估算 token | 上下文提供者 / Harness 压缩 |
| 成本预留/结算 | `ASSISTANT_USAGE_DAY` + `TryReserveAsync` | 无内置，需自定义 |
| 失败熔断 | `FailureBreaker` | 无内置，需自定义 |
| 工具调用标记清理 | `ToolCallTextGuard` | 无内置，需自定义 |
| 审计 | `WorkbenchAuditWriter` + `ACTOR_TYPE=Agent` | 无内置，需自定义 |
| 记忆 | `ASSISTANT_MEMORY` + `MemoryDistiller` | `AIContextProvider` |
| 知识库 | `KB_*` + `KbSearchTool` | `AIContextProvider` / 向量库连接器 |
| 评估 | `AssistantEval` 8 组种子 | 无内置评测口径 |

**结论：EOS 不是"没有 Agent 框架"，而是"有一个只服务 EOS 的 Agent 框架"。** 现在要决定的是：继续维护这个私有框架，还是把引擎换成 MAF、把治理壳抽成可复用件。

### 1.5 评估与运维现状（迁移前必须知道）

- `EOS.API.Tests/AssistantEval/` 有 8 组种子：`situation`、`diagnosis`、`action`、`config`、`authz`、`inference`、`functional`、`business`。
- 这些种子主要做**确定性断言**（越权 0 泄露、闭集对齐、来源锚定、格式），**不做自然语言回答正确率**；后者仍需 LLM-as-judge，尚未就绪。
- `docs/status.md` §4.2 明确：AI 试点复核自 2026-08-07 基线后未复跑，用户已指示暂缓。
- 也就是说：**今天没有一套"换引擎后行为是否等价"的自动化证据**。这是迁移最大的前置缺口，必须先补。

---

## 2. 为什么当时没有选择 MAF

### 2.1 仓库证据：不是"评估后否决"，是"没有评估"

> 说明：以下是**从仓库证据反推**，不是对当时 AI 对话记录的还原；如果当时的会话里有额外判断，以原始记录为准。

我查了三条线：

1. `git log --all --grep='Agent Framework' --grep='Semantic Kernel' --grep='AutoGen' -i`：**0 条**；
2. 全仓库 `*.csproj` 的 `PackageReference`：**没有任何** `Microsoft.Agents.AI` / `Microsoft.SemanticKernel`；
3. 所有 ADR 与 `docs/plans/`：**没有** MAF 选型记录；ADR-007 的"考虑过的替代方案"列了 A–E 五项，没有一项是"采用外部 Agent 框架"。

所以，准确的说法不是"AI 认为 MAF 不好"，而是：

> **当时根本没有做"用不用 Agent 框架"这道选择题。**

### 2.2 当时的真实决策：要不要独立编排器

ADR-007 的背景是：`EOS.Client` 下线，原本为"Agent 平台化"预制的三进程链路（Web → AgentRuntime.Host → Gateway）失去主要消费方。决策的核心是：

- 保留三进程编排链路？→ 否，YAGNI，平台化被推迟到阶段 F；
- 还是把推理收进 `EOS.API`，用一条 `ChatService` 完成？→ 是。

ADR-007 §4 的原话是：

> "工作助手没有计划/确认点/恢复那套编排需求，一条 ChatService 足够。"

这句话就是 MAF 没有进入候选的根本原因：**MAF 的核心卖点（多 Agent、图工作流、检查点、托管）不在当时的需求里；而当时的硬需求（权限、写管线、审计、成本）MAF 一个都不提供。** 在一个"只需要单 Agent 工具循环"的项目里，引入一个"以编排见长"的框架，收益看不见，成本却立刻发生。

### 2.3 当时合理的六个理由

| # | 理由 | 当时是否成立 | 今天是否仍成立 |
|---|---|---|---|
| 1 | **范围**：单 Agent、单轮问答 + 工具，没有多 Agent/长流程 | 成立 | 基本成立；但"可复用"改变了框架的价值 |
| 2 | **安全**：API 是最终权限边界，模型只能调白名单工具 | 成立 | **仍成立**；MAF 不提供授权 |
| 3 | **供应商**：DeepSeek/Kimi/GLM/Qwen 全是 OpenAI 兼容，自写 300 行就能覆盖 | 成立 | 基本成立；MAF 仍需 `IChatClient` 适配 |
| 4 | **治理**：成本预留、熔断、审计、工具标记清理都是自研 | 成立 | **仍成立**；MAF 不提供 |
| 5 | **依赖**：1.x 版本节奏快，安全关键路径不宜追新 | 成立 | 部分成立；1.25 更成熟，但仍平均约 8 天一个 minor |
| 6 | **评估**：没有换引擎的等价性证据 | 成立 | **仍成立**；必须先补 |

第 5 条要特别说明：MAF 在 2026-04-02 GA，助手在 2026-08-25 开工，中间只有 4 个多月；到 2026-10 已到 1.25.0，约 6 个月 25 个 minor（平均约 8 天一个）。对一个大版本稳定、发布窗口固定的 ERP 来说，"持续跟进一个快速演进的 Agent 框架"是真实的维护成本，不是借口。

### 2.4 哪些理由今天不再成立

| 当时的理由 | 今天的变化 |
|---|---|
| "MAF 主要面向多 Agent/托管，单 Agent 用不上" | MAF 的单 Agent + 工具 + 中间件 + 会话已经成熟；`ChatClientAgent` 就是为单 Agent 设计的 |
| "MAF 太新，不敢放在安全关键路径" | 1.x 已 GA 约 6 个月，核心包已过 preview 期；MIT、net10.0 原生支持 |
| "自己写循环更可控" | 循环本身已稳定，但**跨项目复用**时，私有循环是负债；标准引擎 + 自有治理壳更可控 |
| "引入框架会削弱安全边界" | 只要 MAF 只做引擎、工具仍走 EOS 授权，边界不变；middleware 是新增的观测/治理缝，不是权限替代 |
| "没有评估证据" | 这是今天**必须补**的，而不是不换的理由；先建 parity harness 再换 |

### 2.5 一句话回答

> **当时不是 MAF 不够好，而是这个项目当时要的不是一个 Agent 框架，而是一个受控的 ChatService。** MAF 的价值点（编排、工作流、多智能体、托管）不在需求里；它的成本（新依赖、适配、学习、版本节奏）在需求里。今天你的目标从"交付一个助手"变成"积累一套可复用框架"，这道题的答案才变。

---

## 3. MAF 今天是什么（截至 2026-10）

### 3.1 版本、许可、平台

| 项 | 事实 | 对 EOS 的意义 |
|---|---|---|
| 最新稳定版 | **1.25.0**（截至 2026-10-23） | 可锁定版本，按季度升级 |
| 1.0 GA | **2026-04-02** | 助手开工前已 GA |
| 许可 | **MIT** | 商用无额外许可成本 |
| 目标框架 | `net8.0` / `net9.0` / `net10.0` / `netstandard2.0` / `net472` | EOS 的 `net10.0` 原生支持 |
| 核心包 | `Microsoft.Agents.AI`、`.Abstractions`、`.OpenAI`、`.Workflows` | 只需核心 + 可选 OpenAI/Workflows |
| 托管包 | `Microsoft.Agents.AI.Hosting*` 仍为 **prerelease** | **生产不用** |
| 耐久扩展 | `Microsoft.Agents.AI.DurableTask` 独立仓库，面向 Durable Task Scheduler / Azure Functions；本地集成测试还要 Azurite / Redis | **本地 IIS + SQL Server 不用** |
| Agent Hooks | .NET **尚不可用**（Python only，实验性） | 用 middleware 代替 |
| Harness | .NET 可用，但自带 Shell、文件、Web 搜索、Todo 等 | **生产不用**；能力面太宽 |

> 依据：[MAF Overview](https://learn.microsoft.com/en-us/agent-framework/overview/agent-framework-overview)、[Model providers](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/)、[Self-hosting](https://learn.microsoft.com/en-us/agent-framework/hosting/self-hosting/)、[Durable Extension](https://github.com/microsoft/agent-framework-durable-extension)、[NuGet](https://www.nuget.org/packages/Microsoft.Agents.AI)。

### 3.2 能力地图（与 EOS 的相关性）

| 能力 | MAF 提供什么 | 与 EOS 的相关性 | 建议 |
|---|---|---|---|
| 单 Agent | `AIAgent` / `ChatClientAgent` | 高：直接替代 `ChatService` 循环 | **用** |
| 工具 | `AIFunction`、`AIFunctionFactory`、`ApprovalRequiredAIFunction` | 高：31 个工具可包装 | **用** |
| 中间件 | Agent run / Function calling / IChatClient 三层 | 高：审计、成本、限流、脱敏 | **用** |
| 会话 | `AgentSession`、`ChatHistoryProvider`、`AIContextProvider` | 中：EOS 已有 DB 会话 | **不持久化，按请求构造** |
| 工作流 | `WorkflowBuilder`、Executor、边、检查点、人工介入 | 中高：适合确定性多步流程 | **试点后用** |
| 多 Agent | 顺序、并发、Handoff、GroupChat、Magentic | 低：EOS 当前不需要 | **暂不用** |
| 可观测性 | OpenTelemetry / `gen_ai.*` | 中：EOS 有自研结构化日志 | **桥接，不替代** |
| 托管 | Foundry Hosted、A2A、OpenAI 兼容端点 | 低：EOS 自托管 | **不用** |
| 声明式 Agent | YAML 定义 | 低：EOS 配置在 DB | **不用** |
| Agent Skills | 文件/内联/类库 | 低：EOS 已有工具注册表 | **暂不用** |
| DevUI | 本地调试 UI | 低：开发辅助 | **可选** |
| MCP | 本地/托管 MCP 工具 | 中：未来可把 EOS 工具暴露给其他 Agent | **以后再说** |

### 3.3 与 Semantic Kernel / AutoGen 的关系

MAF 是 Microsoft 对 Semantic Kernel 与 AutoGen 的**继任者**：把 AutoGen 的 Agent 抽象与 Semantic Kernel 的企业特性（会话状态、类型安全、过滤器、遥测、模型/嵌入支持）合并，再加上图工作流与长流程状态管理。对新项目，直接选 MAF 比选 SK 更合适；对已有 SK 项目，官方有迁移指南。

对 EOS 的意义：**不需要先迁 SK 再迁 MAF**。EOS 没有 SK 依赖，这是一张白纸，反而简单。

**选型对比（为什么是 MAF，而不是别的）：**

| 方案 | 适合 | 不适合 |
|---|---|---|
| 直接 `IChatClient` | 单轮/少工具/无会话的轻量场景 | 多轮工具、治理、跨项目复用 |
| 自研循环（EOS 现状） | 安全极端定制、不想引入外部依赖 | 跨项目复用、工作流、生态/交接 |
| **MAF** | 单/多 Agent、工具、中间件、工作流、多供应商 | 权限/写管线/审计（必须自建） |
| Semantic Kernel | 已有 SK 投资、迁移过渡 | 新项目（MAF 是官方继任者） |
| AutoGen | 研究型多 Agent 实验 | 企业 .NET 生产（MAF 已合并其抽象） |

### 3.4 .NET 侧的成熟度边界（重要）

MAF 的"能力"和"生产可用"要分开看：

- **核心 Agent/工具/中间件/工作流**：1.x 稳定，可用；
- **Hosting**：.NET 包仍 prerelease，且明确说"不是 HTTP 服务器/协议注册中心"，要求你自己处理认证、授权、路由、存储；
- **Durable**：面向 Azure Durable Task Scheduler / Azure Functions，本地 on-prem 要自建等价基础设施；
- **Agent Hooks**：.NET 没有，fail-closed 的统一控制面用不了；只能用 middleware + 工具审批；
- **Harness**：适合"长任务、会读写文件、跑 Shell"的编码型 Agent，不适合 ERP 业务助手；默认还会加 Web 搜索，必须关掉。

**所以"全面使用 MAF"必须限定为"核心引擎 + 可选工作流"，不能把托管/耐久/Harness 一起算进来。**

---

## 4. 可行性评估

### 4.1 适配矩阵

| 需求 | MAF 覆盖 | EOS 现状 | 适配度 | 结论 |
|---|---|---|---|---|
| 多轮工具调用 | `FunctionInvokingChatClient` | 自研循环 | 5/5 | 直接替代 |
| 工具声明/JSON Schema | `AIFunction` / `AIFunctionDeclaration` | 手写 Schema | 4/5 | 需自定义 `AIFunction` 保留原 Schema |
| 流式文本/推理 | `AgentResponseUpdate` / `TextReasoningContent` | `ChatDelta` | 4/5 | 需事件映射 |
| 工具开始/结束事件 | `FunctionCallContent` / `FunctionResultContent` | `ToolStarted` / `ToolFinished` | 4/5 | 需映射与去重 |
| 会话持久化 | `AgentSession` / `ChatHistoryProvider` | SQL 表 | 5/5 | **继续用 SQL，不持久化 MAF Session** |
| 权限/数据范围 | 无 | `IPermissionService` + 工作台 | 2/5 | **必须保留 EOS** |
| 写管线/幂等/审批卡 | 无 | 统一表单 + 动作层 | 2/5 | **必须保留 EOS** |
| 成本预留/结算 | 无 | `ASSISTANT_USAGE_DAY` | 3/5 | middleware + 现有台账 |
| 熔断/限额 | 无 | `FailureBreaker` | 3/5 | middleware + 现有服务 |
| 审计 | 无 | `WorkbenchAuditWriter` | 3/5 | function middleware + 现有写入 |
| 工具调用标记清理 | 无 | `ToolCallTextGuard` | 3/5 | 流式后处理 |
| 工作流/检查点 | `WorkflowBuilder` + `CheckpointManager` | 无 | 4/5 | 新场景试点 |
| 本地 on-prem | 核心可用 | IIS + SQL Server | 4/5 | 不用 Azure 组件即可 |
| 跨项目复用 | 标准抽象 | 私有实现 | 5/5 | **最大收益** |

**加权可行性：3.8 / 5。** 评分是判断，不是实测；权重：技术兼容 20%、功能覆盖 20%、安全合规 20%、运维 15%、迁移成本 15%、复用价值 10%。

> 计算：5×0.20 + 4×0.20 + 3×0.20 + 4×0.15 + 2×0.15 + 5×0.10 = **3.8**。

**结论：可行，但必须带壳；不是"全量重写"的可行，是"分层替换"的可行。**

### 4.2 技术可行性：五个关键接缝

#### 接缝 1：模型接入 —— 先适配 `IChatClient`，不要先换传输

EOS 现在所有供应商都走 OpenAI 兼容 `/chat/completions`，自写了 SSE 解析、`reasoning_content` 透传、错误分类（401/402/429/413/5xx + 文本特征）。MAF 支持任何 `IChatClient`，所以最稳的路径是：

```
现有 IChatModel  →  ChatModelChatClient（IChatClient 适配器）  →  MAF ChatClientAgent
```

而不是一上来换成 `OpenAI` SDK。原因：

- DeepSeek 的 `reasoning_content` 不是 OpenAI 标准字段，SDK 映射不一定保留；
- 现有错误分类是产品文案的一部分（`AI_MODEL_UNAUTHORIZED` / `AI_MODEL_INSUFFICIENT_BALANCE` / `AI_CONTEXT_LENGTH_EXCEEDED`），换 SDK 会丢；
- 现有模型行支持运行期切换、上下文窗口、单价、是否支持工具；这些是 DB 数据，不是 SDK 配置；
- 适配器可以先把行为固定下来，等 MAF 路径稳定后，再按供应商逐个换原生连接器。

**代价**：多一层适配器（约 300–500 行），但这是"防腐层"，不是临时补丁。

#### 接缝 2：工具 Schema —— 必须原样保留

EOS 的工具 Schema 是服务端硬编码的，31 个工具的形状经过真实模型验证（DeepSeek 对 Schema 很敏感）。MAF 的 `AIFunctionFactory.Create` 会从方法签名反射生成 Schema，**不能保证与现有 Schema 逐字一致**。

所以需要一个自定义 `AIFunction` 子类，直接持有现有 `ParametersJson`：

```csharp
// 伪代码：以 MAF 1.25 实际 API 为准
public sealed class JsonSchemaAIFunction : AIFunction
{
    private readonly ToolDescriptor _descriptor;
    private readonly JsonElement _schema;
    private readonly Func<JsonElement, CancellationToken, ValueTask<ToolResult>> _invoke;

    public override string Name => _descriptor.Name;
    public override string Description => _descriptor.Description;
    public override JsonElement JsonSchema => _schema;

    // AIFunction.InvokeAsync 非 virtual；自定义实现必须覆盖 InvokeCoreAsync
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToElement(
            arguments.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value));
        var result = await _invoke(json, ct);
        return result.ContentForModel;
    }
}
```

这一步是"能不能等价"的关键。**Schema 差一个字段，模型行为就会变。**

#### 接缝 3：每次调用身份 —— 幂等键不能丢

EOS 的写工具 `apply_record_action` 依赖 `IToolCallContextTool.UseToolCallContext(sessionId, toolCallId)` 推导幂等键；模型不能生成或伪造这个键。MAF 的 `FunctionCallContent.CallId` 是天然来源，但需要从 `FunctionInvokingChatClient` 传到工具实例。

两条可行路径：

1. `FunctionInvoker` 包装：在调用前把 `CallId` 放进 `AsyncLocal<ToolCallContext>`；
2. `FunctionInvokingChatClient.CurrentContext`：在 `AIFunction.InvokeCoreAsync` 里读当前 `FunctionInvocationContext`。

**必须写一条契约测试**：同一个 `CallId` 连续调用两次，幂等键一致；不同 `CallId` 不同。否则写路径不能灰度。

#### 接缝 4：轮次/并行/收尾 —— 语义要对齐

EOS 的循环有三条特殊语义：

1. `MaxToolRounds` 到达后，**强制纯文本收尾**，并注入 `FinalRoundInstruction`，否则 DeepSeek 会把工具调用标记写进正文；
2. 只有**全部只读且不重名**的工具才并行；
3. 工具轮的结果只存在于内存上下文，不落库；最终回复才落库。

MAF 的 `FunctionInvokingChatClient` 有 `MaximumIterationsPerRequest`、`AllowConcurrentInvocation`、`MaximumConsecutiveErrorsPerRequest`、`TerminateOnUnknownCalls`，但不是逐条等价：

- `AllowConcurrentInvocation` 是全局开关，不知道"只读/写"的风险分级；**初期必须设为 `false`**，否则写工具可能并发；
- `MaximumIterationsPerRequest` 的计数口径要以 spike 实测为准：源码在 `iteration >= MaximumIterationsPerRequest` 时移除工具并做最后一次纯文本调用，按此推演 `MaxToolRounds=4` 对应 `4`（4 轮工具 + 1 次收尾），不要机械 +1；
- MAF 在最后一次迭代会**移除工具声明**（`PrepareOptionsForLastIteration`），与 EOS 的"最后一条不带工具"一致；但它**不会注入** EOS 的 `FinalRoundInstruction`，要用 Agent run middleware 补；
- 并行优化会暂时丢失（读工具串行，延迟增加）；后续可用自定义 `FunctionInvoker` 按风险分级恢复。

**建议**：第一版接受"工具串行"，先保正确性；把并行优化列为 P2，不阻塞迁移。

#### 接缝 5：流式事件与 SSE —— 外部契约不变

前端只认 `delta` / `reasoning` / `tool_start` / `tool_result` / `done` / `error` 六种事件。MAF 的 `AgentResponseUpdate` 是内容模型，需要映射：

| MAF 内容 | EOS 事件 |
|---|---|
| `TextContent` | `delta`（先过 `ToolCallTextGuard`） |
| `TextReasoningContent` | `reasoning` |
| `FunctionCallContent` | `tool_start` |
| `FunctionResultContent` | `tool_result` |
| `UsageContent` | 累加，不直接下发 |
| 流结束 | `done`（带 `ASSISTANT_MESSAGE`、工具摘要、草稿） |
| 异常 | `error`（复用现有错误分类） |

**外部契约不变，前端零改动**，这是本次迁移的最低成本原则。

### 4.3 安全可行性：MAF 不是安全边界

必须把这句话写在最前面：

> **MAF 不提供认证、授权、租户隔离、字段级权限、数据范围、写管线、审计身份。它只提供执行骨架。**

因此：

| 安全项 | 谁负责 | 迁移后的变化 |
|---|---|---|
| 登录/Cookie | EOS | 不变 |
| 模块/操作位/字段/数据范围 | EOS `IPermissionService` + 工作台 | 不变 |
| 工具白名单 | EOS `AssistantToolRegistry` | 不变，包成 `AIFunction` 列表 |
| 工具参数校验 | EOS 工具内部 | 不变；MAF 只做 Schema 声明 |
| 写管线/幂等/审计 | EOS `WorkbenchCommandHandler` | 不变 |
| 模型输出不进 SQL | EOS 工具层 | 不变 |
| 成本/熔断 | EOS + middleware | 新增 middleware 采集，台账不变 |
| 审计 | EOS `WorkbenchAuditWriter` | function middleware 调用现有写入 |
| 提示注入隔离 | EOS 提示词组装 | 不变；MAF 不解决 |

**middleware 是"观测与治理缝"，不是"权限边界"。** 任何"把授权放进 MAF middleware"的设计都应否决：middleware 可以被绕过（自定义 Agent、直接调 `IChatClient`），而 EOS 的授权必须在工具内部、每次执行时重新判定。

MAF 的 `ApprovalRequiredAIFunction` 倒是一个有价值的补充：它能把"高风险工具执行前必须人工确认"变成结构化的 `ToolApprovalRequestContent`，而不是靠模型"听懂"用户的"好的"。EOS 当前的 `apply_record_action` 是模型可直接调用的写工具；它是否构成 ADR-007 §5 第 5 条所说的"高风险动作"可以讨论，但**删除类动作**至少值得结构化确认。**这是独立于 MAF 的既有问题；如果迁移，可以顺手用工具审批补上，但不要把它当成迁移的卖点。**

### 4.4 运维可行性：本地 IIS + SQL Server 的硬约束

| 约束 | MAF 影响 | 结论 |
|---|---|---|
| Windows / IIS，无 Azure | 核心包纯进程内，无 Azure 依赖 | 可用 |
| SQL Server 是唯一库 | MAF 无内置 SQL Session/Checkpoint Store | 自己实现，或不用 |
| 进程回收/重启 | 内存中的 AgentSession/Workflow 会丢 | 交互式请求可接受；长流程需 SQL checkpoint |
| 无 Redis/Cosmos/Azurite | Durable Task 扩展面向 Azure 托管/函数宿主，本地测试还要这些组件 | **不用 Durable** |
| 无容器编排 | Foundry Hosted/A2A 托管不适用 | **不用托管** |
| WDAC 构建限制 | 新增 NuGet 依赖可能带来加载问题 | 先在独立 spike 验证；不要直接改 EOS |
| 服务由用户人工重启 | 迁移期间的开关要能热切换或明确重启 | 用部署开关 + 文档化重启步骤 |

**最重要的运维结论：EOS 不需要为了 MAF 新增任何基础设施。** 核心 Agent 是进程内库；SQL 仍是唯一持久化。只要不碰 Hosting/Durable/Harness，运维面不变。

### 4.5 经济性：替换 1 千行，保留 1.6 万行

| 方案 | 工作量 | 风险 | 复用价值 | 结论 |
|---|---:|---|---|---|
| A. 维持现状 | 0 | 低 | 低（仅 EOS） | 可接受，但复用目标无法达成 |
| B. 全量重写 | 10–12 周 | 高（行为回归、双栈） | 中（仍带 EOS 私有治理） | 不推荐 |
| C. 引擎+壳，渐进迁移 | 8–12 周 | 中（可控灰度） | **高** | **推荐** |
| D. 只在新场景用 MAF | 2–3 周 | 低 | 低（两套栈） | 可作为 C 的第一步 |

**ROI 判断：**

- 只算 EOS：迁移的直接收益是"少维护 1 千行循环"，但 EOS 部分的一次性成本 6–8 周（方案 C 含抽包为 8–12 周），**短期为负**；
- 算上"可复用 Agent Harness + 后续 2–3 个企业项目"：每个项目省 2–4 周脚手架 + 治理代码，**6–12 个月回本**；
- 算上"招人/交接/生态"：MAF 是 Microsoft 维护的主流 SDK，比私有循环更容易被后来者理解，**长期为正**。

所以，**不要把这次迁移包装成"EOS 的性能/功能优化"；它是一次"平台化投资"。** 如果只想让 EOS 更好用，不迁也能活；如果想让 Agent 能力成为你的可复用资产，就值得做。

### 4.6 风险清单

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| 工具循环行为回归（轮次/收尾/并行） | 高 | 中 | 录制回放 parity、灰度、保留旧引擎 |
| DeepSeek 推理内容/错误分类丢失 | 中 | 高 | `IChatModel` 适配器 + 契约测试 |
| 工具 Schema 变形 | 中 | 高 | 自定义 `AIFunction` 原样持有 Schema；逐工具快照比对 |
| 幂等键/调用身份丢失 | 中 | 高 | `CallId` 透传 + 写路径契约测试 |
| 版本升级破坏 | 中 | 中 | 锁定 1.25.x，季度升级，适配层隔离 |
| 双栈长期并存 | 高 | 中 | 设 2 周删除窗口；旧引擎只读回滚 |
| 团队学习成本 | 中 | 低 | spike + 样例 + 内部文档 |
| 安全回归 | 低 | 致命 | 不改授权路径；越权用例 0 泄露；红队复核 |
| 成本/延迟上升 | 中 | 中 | 用量 middleware、预算、并行优化 |
| 过度抽象 | 中 | 中 | 先 EOS 一个宿主，出现第二个宿主再抽包 |

### 4.7 "全面使用"的五个级别（含现状）

| 级别 | 含义 | 可行性 | 建议 |
|---|---|---|---|
| L0 | 不用 MAF | 100% | 现状 |
| L1 | 用 MAF 替换 Agent 循环/工具/中间件 | 高 | **EOS 迁移目标** |
| L2 | L1 + 用 MAF Workflows 做确定性多步流程 | 中高 | **Agent Harness 第二阶段** |
| L3 | L2 + Hosting/A2A/Durable/Foundry | 低（本地部署） | 仅云上项目 |
| L4 | 多 Agent 平台/Agent 市场 | 低（当前需求） | 不做 |

**"全面使用"建议定义为 L2，而不是 L3/L4。** L2 已经能覆盖企业管理系统 90% 的 Agent 需求；L3/L4 会把复杂度带到运维和权限面，收益与风险不匹配。

### 4.8 结论

> **有条件可行。** 技术上，MAF 核心可以在 .NET 10 / IIS / SQL Server / 无 Azure 的形态下运行；工程上，它能替换 Agent 循环与工具调用骨架，但不能替换 EOS 的权限、写管线、审计与成本治理；经济上，单独为 EOS 做全量重写不划算，但作为"可复用 Agent Harness 的引擎"值得投入。
>
> **推荐方案 C：MAF 做引擎、Agent Harness 做壳、EOS 做第一个宿主；先读路径、再写路径；先灰度、再删旧循环。**

---

## 5. 目标架构：Agent Harness

> **定位：不是通用 Agent 框架，而是"企业管理系统里的受控 Agent 运行时"**——强权限、强审计、强写管线、弱多智能体。通用能力交给 MAF；Agent Harness 只补 MAF 不提供的那一层。

### 5.1 设计原则

1. **MAF 是引擎，不是架构。** 领域代码不出现 `AIAgent` / `ChatMessage` / `AgentSession` 类型；它们只存在于 `Agent.Harness.Maf`。
2. **EOS 数据库是唯一事实源。** 会话、消息、用量、记忆继续落 SQL；不引入第二套 MAF Session 存储。
3. **防腐层优先。** 保留 `IChatModel`、`IAssistantTool`、SSE 契约；MAF 通过适配器接入。
4. **安全留在 EOS。** 工具每次执行重新授权；MAF middleware 只做观测、成本、限流、脱敏。
5. **一个引擎，多个宿主。** `Agent Harness` 不依赖 EOS；EOS 只是第一个 `Host`。
6. **工作流只用于确定性多步。** 语言推理用 Agent；固定步骤用 Workflow；不要把所有流程都做成 Agent。
7. **锁定版本，延迟升级。** 核心包按季度升级；升级前跑 parity + 安全回归。
8. **不为不存在的需求建能力。** 多 Agent、A2A、MCP、Durable 都等到第二个真实场景再开。

### 5.2 分层图

```text
EOS.Web（不改：悬浮抽屉 / 全屏页 / SSE 六种事件）
   │ HTTP + SSE（契约不变）
   ▼
EOS.API（AssistantController 不改）
   ├─ ChatService：会话 / 提示词 / 落库 / 预算 / 审计 / 记忆 / 处境
   └─ Tools/：31 个工具（权限 / 写管线 / 幂等）
   │ IAgentEngine（新接口）
   ▼
Agent.Harness.Core
   ├─ TurnPipeline：预检 → 预算预留 → 上下文 → 引擎 → 结算
   ├─ ToolRegistry / ToolPolicy / CostMeter / AuditSink / Breaker
   └─ AgentEvent（与 SSE 同构）
   │
   ▼
Agent.Harness.Maf
   ├─ MafAgentEngine（ChatClientAgent + FunctionInvokingChatClient）
   ├─ ChatModelChatClient（IChatModel → IChatClient）
   ├─ JsonSchemaAIFunction（原样保留工具 Schema）
   └─ GovernanceMiddleware / EventMapper / UsageCollector
   │
   ▼
现有 IChatModel → DeepSeek / Kimi / GLM / Qwen / OpenAI 兼容
现有 EOS 工具 → 权限 / 工作台 / 写管线 / 审计

唯一事实源（旁路）：SQL Server
  ASSISTANT_SESSION / ASSISTANT_MESSAGE / ASSISTANT_USAGE_DAY /
  ASSISTANT_MEMORY / AUDIT_EVENT
不引入：MAF Session Store / Checkpoint Store / Cosmos / Redis
```

### 5.3 包与职责

| 包 | 目标框架 | 依赖 | 职责 | 何时抽 |
|---|---|---|---|---|
| `Agent.Harness.Abstractions` | `net8.0;net10.0` | 无 | 契约：`IAgentEngine`、`ITool`、`IToolPolicy`、`AgentEvent`、`ITurnBudget`、`IAuditSink` | 阶段 1 |
| `Agent.Harness.Core` | `net8.0;net10.0` | Abstractions | 轮次管线、工具注册表、成本/熔断/脱敏、事件流、SSE 映射 | 阶段 1 |
| `Agent.Harness.Maf` | `net8.0;net10.0` | Core + `Microsoft.Agents.AI` | MAF 适配：`ChatClientAgent`、`IChatClient`、`AIFunction`、middleware | 阶段 2 |
| `Agent.Harness.AspNetCore` | `net8.0;net10.0` | Core | `MapAgentHarness`、DI、健康检查、SSE 写出 | 阶段 4 |
| `Agent.Harness.Testing` | `net8.0;net10.0` | Core | FakeChatModel、录制/回放、parity 断言 | 阶段 1 |
| `Agent.Harness.Workflows` | `net8.0;net10.0` | Core + `Microsoft.Agents.AI.Workflows` | 工作流封装、检查点、人工介入 | 阶段 5（可选） |

**不要一开始就建 6 个包。** 阶段 1 只建 `Abstractions + Core + Testing`；`Maf` 在阶段 2 加；`AspNetCore`/`Workflows` 在第二个宿主出现后再抽。过早拆包会让接口为"想象中的复用"服务。

### 5.4 核心契约（示意）

```csharp
namespace Agent.Harness.Abstractions;

public sealed record AgentTurnRequest(
    string TenantId,
    string UserId,
    string ConversationId,
    string Input,
    IReadOnlyList<AgentMessage> History,
    AgentPageContext? PageContext,
    string CorrelationId,
    string SystemPrompt,     // 由 ChatService 组装（含处境/记忆/规则）
    int MaxToolRounds);      // 由参数目录快照给出

// AgentMessage / AgentPageContext / AgentUsage / ToolCallRecord / BudgetLease
// 是示意类型；实际实现按 EOS 现有 DTO 映射，不引入 MAF 类型。

public abstract record AgentEvent
{
    public sealed record TextDelta(string Text) : AgentEvent;
    public sealed record ReasoningDelta(string Text) : AgentEvent;
    public sealed record ToolStarted(string CallId, string Name, ToolRisk Risk) : AgentEvent;
    public sealed record ToolFinished(string CallId, string Name, string Digest, bool Ok) : AgentEvent;
    public sealed record Completed(AgentTurnResult Result) : AgentEvent;
    public sealed record Failed(string Code, string Message) : AgentEvent;
}

public sealed record AgentTurnResult(
    string Text,
    string ModelName,
    AgentUsage Usage,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyList<object> Drafts,
    string? FinishReason);

public interface IAgentEngine
{
    IAsyncEnumerable<AgentEvent> StreamAsync(
        AgentTurnRequest request, CancellationToken cancellationToken);
}
```

工具契约：

```csharp
public enum ToolRisk { Read, Draft, Write, AdminWrite }

public sealed record ToolDescriptor(
    string Name,
    string Description,
    string ParametersJson,
    ToolRisk Risk,
    bool ReadOnly,
    bool AllowParallel);

public sealed record ToolInvocation(
    string TenantId,
    string UserId,
    string ConversationId,
    string CallId,
    JsonElement Arguments,
    AgentPageContext? PageContext);

public interface ITool
{
    ToolDescriptor Descriptor { get; }
    ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct);
}

public sealed record ToolResult(bool Ok, string ContentForModel, object? Draft = null);
```

策略与治理：

```csharp
public interface IToolPolicy
{
    ValueTask<ToolDecision> EvaluateAsync(
        ToolDescriptor tool, ToolInvocation invocation, CancellationToken ct);
}

public abstract record ToolDecision
{
    public sealed record Allow : ToolDecision;
    public sealed record Deny(string Code, string Message) : ToolDecision;
    public sealed record RequireApproval(string CardJson) : ToolDecision;
}

public interface ITurnBudget
{
    ValueTask<BudgetLease> ReserveAsync(AgentTurnRequest request, CancellationToken ct);
    ValueTask SettleAsync(BudgetLease lease, AgentUsage usage, bool completed, CancellationToken ct);
}

public interface IAuditSink
{
    ValueTask WriteAsync(AgentAuditEvent evt, CancellationToken ct);
}
```

**设计要点：**

- `AgentEvent` 与 SSE 事件同构，但**不绑定 HTTP**；控制台/后台任务也能用；
- `ITool` 不依赖 MAF；`Agent.Harness.Maf` 把它包成 `AIFunction`；
- `IToolPolicy` 是"执行前"的门禁；执行后仍由工具内部重新授权，双保险；
- `ITurnBudget` 把"预留/结算"从循环里拿出来，旧引擎/新引擎都能用。

### 5.5 MAF 适配层（示意）

> 以下代码是**结构示意**，不是逐字可编译的成品；API 签名以 MAF 1.25 为准。真正要固定的是分层与接缝，不是某一行代码。

#### `ChatModelChatClient`：`IChatModel` → `IChatClient`

```csharp
public sealed class ChatModelChatClient(IChatModel model) : IChatClient
{
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var eosMessages = messages.Select(ToEosMessage).ToList();
        var tools = options?.Tools?
            .OfType<AIFunctionDeclaration>()
            .Select(t => new ToolDefinition(t.Name, t.Description ?? "", t.JsonSchema.GetRawText()))
            .ToList();

        var calls = new SortedDictionary<int, CallAccumulator>();
        await foreach (var delta in model.StreamAsync(eosMessages, tools, ct))
        {
            if (!string.IsNullOrEmpty(delta.ContentDelta))
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new TextContent(delta.ContentDelta)]);

            if (!string.IsNullOrEmpty(delta.ReasoningDelta))
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new TextReasoningContent(delta.ReasoningDelta)]);

            if (delta.ToolCallDeltas is not null)
                foreach (var f in delta.ToolCallDeltas)
                {
                    if (!calls.TryGetValue(f.Index, out var acc))
                    {
                        acc = new CallAccumulator();
                        calls[f.Index] = acc;
                    }
                    acc.Append(f);
                }

            if (delta.Usage is not null)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new UsageContent(new UsageDetails
                    {
                        InputTokenCount = delta.Usage.PromptTokens,
                        OutputTokenCount = delta.Usage.CompletionTokens,
                    })]);

            if (delta.FinishReason is not null && calls.Count > 0)
            {
                foreach (var call in calls.Values)
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        [new FunctionCallContent(call.Id!, call.Name!, call.ParseArguments())]);
                calls.Clear();
            }
        }

        // 部分厂商不发 finish_reason：流结束兜底，避免工具调用丢失
        foreach (var call in calls.Values)
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent(call.Id!, call.Name!, call.ParseArguments())]);
    }

    // GetResponseAsync 省略：聚合 streaming 结果即可
}
```

**要点：**

- 工具调用的 `arguments` 是分片 JSON，必须**聚合完再解析**；不能在分片中途构造 `FunctionCallContent`；
- `ChatResponseUpdate` 的构造签名以 1.25 实际 API 为准；
- `ToEosMessage` 要处理 `FunctionCallContent` / `FunctionResultContent`，不能只处理文本；
- 适配器只做协议转换，不做权限/落库/提示词——与现有 `IChatModel` 契约一致。

#### `JsonSchemaAIFunction`：保留原工具 Schema

见 §4.2 接缝 2。补充：

```csharp
var tools = registry.Definitions
    .Where(d => capability.IsToolEnabled(d.Name))
    .Select(d => new JsonSchemaAIFunction(
        d,
        async (args, ct) =>
        {
            var invocation = new ToolInvocation(
                tenantId, userId, conversationId,
                callContext.CurrentCallId, args, pageContext);
            var decision = await policy.EvaluateAsync(d, invocation, ct);
            if (decision is ToolDecision.Deny deny)
                return new ToolResult(false, deny.Message);
            return await tool.InvokeAsync(invocation, ct);
        }))
    .Cast<AITool>()
    .ToList();
```

**要点：**

- 每个请求构造一次工具列表，捕获 `userId` / `conversationId` / `pageContext`；
- `CallId` 从 `FunctionInvokingChatClient.CurrentContext?.CallContent.CallId` 取（不要从模型参数里取）；它同时用于工具事件与幂等键；
- `ToolResult.Draft` 写入 scoped `DraftCollector`，随 `done` 事件下发；
- 不要用 DI 单例工具 + 反射；工具实例是 Scoped，每请求生命周期明确。

#### `MafAgentEngine`：循环替换

```csharp
public sealed class MafAgentEngine(
    IChatModel model,
    IToolCatalog tools,
    TurnCollector collector,
    ILoggerFactory loggerFactory) : IAgentEngine
{
    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var inner = new ChatModelChatClient(model);
        var invoking = new FunctionInvokingChatClient(inner, loggerFactory)
        {
            MaximumIterationsPerRequest = request.MaxToolRounds, // 4 轮工具 + 1 次收尾，spike 复核
            AllowConcurrentInvocation = false,   // 第一版：安全优先
            MaximumConsecutiveErrorsPerRequest = 2,
            TerminateOnUnknownCalls = true,
            IncludeDetailedErrors = false,
        };

        var agent = new ChatClientAgent(invoking, new ChatClientAgentOptions
        {
            Id = "eos-assistant",
            Name = "EOS Assistant",
            UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions
            {
                Instructions = request.SystemPrompt,
                Tools = tools.BuildAIFunctions(request),
                AllowMultipleToolCalls = true,
            },
        });

        // 系统提示走 ChatOptions.Instructions，避免与手工 system 消息重复
        var messages = request.History.Select(ToMeaiMessage).ToList();

        var text = new StringBuilder();
        var usage = new UsageCollector();
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? finishReason = null;

        await foreach (var update in agent.RunStreamingAsync(messages, session: null, options: null, ct))
        {
            finishReason ??= update.FinishReason?.ToString();
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent t when !string.IsNullOrEmpty(t.Text):
                        text.Append(t.Text);
                        yield return new AgentEvent.TextDelta(t.Text);
                        break;
                    case TextReasoningContent r when !string.IsNullOrEmpty(r.Text):
                        yield return new AgentEvent.ReasoningDelta(r.Text);
                        break;
                    case FunctionCallContent call:
                        callNames[call.CallId] = call.Name;
                        yield return new AgentEvent.ToolStarted(
                            call.CallId, call.Name, tools.RiskOf(call.Name));
                        break;
                    case FunctionResultContent result:
                        yield return new AgentEvent.ToolFinished(
                            result.CallId,
                            callNames.GetValueOrDefault(result.CallId, "unknown"),
                            Digest(result.Result),
                            result.Exception is null);
                        break;
                    case UsageContent u:
                        usage.Add(u.Details);
                        break;
                }
            }
        }

        yield return new AgentEvent.Completed(new AgentTurnResult(
            text.ToString(),
            model.ModelName,
            usage.ToUsage(),
            collector.ToolCalls,
            collector.Drafts,
            finishReason));
    }
}
```

**必须 spike 验证的点：**

1. `FunctionInvokingChatClient` 在最后一次迭代会移除工具声明，但不会注入 EOS 的收尾指令；用 Agent run middleware 补 `FinalRoundInstruction`，并验证 DeepSeek 不再把调用标记写进正文；
2. `FunctionResultContent` 是否真的出现在流里，还是只在 `AgentResponse.Messages` 里；如果不在流里，`tool_result` 事件要由 function middleware 推送；
3. `FunctionCallContent.CallId` 是否能稳定传到工具；如果不行，用 `FunctionInvokingChatClient.CurrentContext`；
4. `UsageContent` 是否每轮模型调用都有；如果只有最后一轮，改用 `IChatClient` middleware 逐调用采集；
5. `AllowMultipleToolCalls` 与 DeepSeek 的兼容性；DeepSeek 对 `tool_choice`/多调用的支持要实测。

### 5.6 与 EOS 的边界（防腐层）

| EOS 侧 | Agent Harness 侧 | 规则 |
|---|---|---|
| `AssistantController` | `Agent.Harness.AspNetCore`（可选） | 控制器不直接依赖 MAF |
| `ChatService` | `IAgentEngine` | `ChatService` 保留会话/提示词/落库/预算/审计，循环委托给引擎 |
| `IChatModel` | `ChatModelChatClient` | 适配器在 `Agent.Harness.Maf`，EOS 不引用 MAF |
| `IAssistantTool` | `ITool` + `JsonSchemaAIFunction` | 工具实现不引用 MAF；适配器在 `Maf` |
| `AssistantToolRegistry` | `IToolCatalog` | 注册表仍是 EOS 的真源；Agent Harness 只读 |
| `ASSISTANT_*` 表 | `IConversationStore` / `IUsageLedger` | EOS 实现，Agent Harness 定义接口 |
| `WorkbenchAuditWriter` | `IAuditSink` | EOS 实现，Agent Harness 调用 |
| `FailureBreaker` | `IBreaker` | EOS 实现，Agent Harness 调用 |

**反例（不要做）：**

- 在 `EOS.API` 的控制器里直接 `new ChatClientAgent(...)`；
- 把 `AIAgent` 放进 `ChatService` 的构造函数；
- 让工具实现引用 `Microsoft.Agents.AI`；
- 把 `AgentSession` 序列化进 `ASSISTANT_SESSION`；
- 用 MAF 的 `ChatHistoryProvider` 替代 EOS 的消息表。

### 5.6.1 跨系统复用：复用壳，不复用业务

| 层 | 复用 | 每个系统自己实现 |
|---|---|---|
| 引擎 | MAF 适配、循环、事件映射 | — |
| 治理 | 预算/熔断/审计/脱敏管线 | 具体台账、审计表、限额来源 |
| 工具 | `ITool` 契约、Schema 包装、策略钩子 | 每个系统的工具实现、权限判定 |
| 会话 | `IConversationStore` 契约 | 表结构、用户隔离、归档策略 |
| 模型 | `IModelProfile` 契约、供应商目录模式 | 密钥、单价、可用型号 |
| 前端 | SSE 事件契约 | 各系统的 UI 壳 |
| 测试 | FakeModel、录制回放、parity | 业务黄金集、越权用例 |

**新系统接入 5 步：**

1. 引用 `Agent.Harness.Core` + `Agent.Harness.Maf`（或只 Core + 自选引擎）。
2. 实现 `ITool`（业务工具）、`IToolPolicy`（权限）、`IConversationStore`、`IAuditSink`。
3. 注册模型 profile（供应商/端点/窗口/单价/是否支持工具）。
4. 组装 system prompt 与上下文提供者（处境、记忆、RAG）。
5. 用内置 SSE 端点或自己的控制器映射 `AgentEvent`；跑 parity + 越权用例。

**不要把 EOS 的 `ASSISTANT_*` 表、模块权限、31 个工具带进 Agent Harness。** 那些是 EOS 的业务，不是框架。

### 5.7 数据与状态

**第一版（阶段 1–3）：零迁移。**

- `ASSISTANT_SESSION` / `ASSISTANT_MESSAGE` / `ASSISTANT_USAGE_DAY` / `ASSISTANT_MEMORY` 不变；
- 每轮从 DB 读历史，构造 `List<ChatMessage>`，传给 `ChatClientAgent`；
- `AgentSession` 不持久化，只在单次请求内使用（或不使用）；
- 工具调用摘要仍写 `ASSISTANT_MESSAGE.TOOL_CALLS_JSON`；
- 成本仍走 `TryReserveAsync` / `SettleAsync`；
- 审计仍写 `AUDIT_EVENT`。

**第二版（阶段 5，仅工作流需要时）：**

- 新增 `AGENT_WORKFLOW_RUN` / `AGENT_WORKFLOW_CHECKPOINT`（或复用 `WORKBENCH_*` 模式）；
- 自定义 SQL `CheckpointManager`；
- 只在"跨请求/跨进程恢复"的真实需求出现时做，不提前建表。

**为什么不让 MAF 管会话：**

- 双写必然不一致；DB 是审计与用户可见历史的真源；
- 模型运行期切换时，`AgentSession` 可能绑定旧模型/旧供应商；
- EOS 的历史消息有 `USER_ID` 隔离、归档、导出、工具摘要等语义，MAF Session 不承载这些；
- 迁移期需要"旧引擎/新引擎读同一份历史"，共享 DB 是唯一简单解。

### 5.8 可观测性与审计

| 关注点 | 实现 |
|---|---|
| 请求链路 | 现有 `correlationId`；`Activity.Current` 加 `gen_ai.*` 标签 |
| 模型调用 | `IChatClient` middleware 记录模型名、耗时、token、finishReason |
| 工具调用 | function middleware 记录工具名、耗时、结果摘要、风险级别 |
| 成本 | `UsageCollector` 逐调用累加；结算仍走 `ASSISTANT_USAGE_DAY` |
| 审计 | `IAuditSink` → `WorkbenchAuditWriter`；`ACTOR_TYPE=Agent` |
| 失败 | 复用 `AssistantModelException` 分类；映射到 `error` 事件 |
| 指标 | `Meter`：`agent.turn.duration`、`agent.tool.duration`、`agent.tokens`、`agent.cost` |

**不要做的：**

- 不要用 MAF 的 OTel 替代现有 JSON 日志；只做补充；
- 不要把完整 prompt / 工具结果写进日志；沿用 `LogRedactor`；
- 不要把 `reasoning_content` 落库（现有设计就是不落库、不回喂）。

### 5.9 测试与评估

| 层 | 工具 | 断言 |
|---|---|---|
| 契约测试 | `Agent.Harness.Testing` | `IChatModel` ↔ `IChatClient` 映射、工具 Schema 逐字一致、事件顺序 |
| 循环 parity | 录制回放 | 同一组模型响应下，旧/新引擎的最终文本、工具调用序列、usage 一致 |
| 工具回归 | 现有 31 工具单测 | 行为不变；写路径幂等键一致 |
| 安全回归 | `AssistantEval/authz` + 新增越权用例 | 0 泄露；工具关闭双向生效 |
| 端到端 | 现有 E2E + 手工冒烟 | SSE 六事件、断流、取消、错误文案 |
| 质量评估 | 人工抽样 + 未来 LLM-as-judge | 回答正确率、拒答率、来源标注 |

**录制回放的关键：在 `IChatModel` 边界录制，不在 HTTP 边界录制。** 这样模型供应商、网络、密钥都无关；回放的是"模型给了什么增量"，测的是"引擎怎么处理"。

```text
录制：真实会话 → 记录每轮 StreamAsync 的 ChatDelta 序列 + 工具结果
回放：FakeChatModel 按序返回 → 分别跑 LegacyEngine / MafEngine
比对：最终文本、工具调用序列、usage、SSE 事件序列、落库消息
```

**不要用真实模型做 parity**：同一 prompt 两次输出不同，无法判定是引擎差异还是模型抖动。

---

## 6. 落地路线图

### 6.1 总览

```text
周次：  1    2    3    4    5    6    7    8    9   10   11   12
P0  spike        ██
P1  契约/回放        ███████
P2  MAF 引擎                 █████████
P3  灰度/切换                          ██████
P4  抽包/第二宿主                            ███████
P5  工作流试点                                      ███████
```

### 6.2 阶段明细

#### P0：独立 spike（第 1 周，5 人日）

**目标：** 在不碰 EOS 的前提下，验证 MAF 1.25 + DeepSeek + 工具 + 流式 + 成本/取消。

**做法：**

- 在独立目录建 console/ASP.NET 项目（**不要放进 EOS 仓库**）；
- 引用 `Microsoft.Agents.AI` 1.25.x（先不引 Hosting/Workflows）；
- 实现 `ChatModelChatClient` 适配器（可先只支持文本 + 工具调用）；
- 用 3 个假工具（1 个只读、1 个写、1 个返回大结果）跑：
  1. 单轮文本；
  2. 单工具调用；
  3. 多轮工具调用（3 轮）；
  4. 一轮多个工具调用；
  5. 工具参数 JSON Schema 与 EOS 一致；
  6. `reasoning_content` 透传；
  7. usage 统计；
  8. 取消/断流；
  9. 401/402/429/413/5xx 错误映射；
  10. 最大轮次收尾行为。

**退出标准：** 10/10 通过；记录 MAF 实际 API 签名、坑、包体积、依赖树；写出 `spike-report.md`。

**失败处理：** 如果第 5/6/10 条无法满足，先停在"适配器 + 自定义函数"层面，不进入 P1；不要为了用 MAF 而改 EOS 的工具 Schema。

> **当前仓库落地说明（2026-10-23）：** P0 实际位于 `spikes/Agent.Harness.Spike`，结果见 [P0 报告](spikes/p0-spike-report.md)。原文写的是"EOS 同级独立目录"方案；本仓库已经是独立的 Agent Harness，不再使用同级目录。

#### P1：契约与回放（第 2–3 周，10 人日）

**目标：** 把 EOS 现有循环的行为固定下来，建立 parity 基线。

**做法：**

- 在 EOS 内新增（不改变行为）：
  - `IAssistantTurnEngine`（或 `IAgentEngine`）；
  - `LegacyTurnEngine`：把 `ChatService` 现有循环原样搬进去，`ChatService` 只保留前置/后置；
  - `TurnTranscript` / `TranscriptRecorder`：在 `IChatModel` 边界录制；
  - `AssistantEngineParityTests`：用录制回放比对。
- 录制至少 50 轮真实/构造会话，覆盖：纯文本、单工具、多工具、多轮、工具拒绝、超时、取消、写工具预览。
- 现有 `AssistantEval` 8 组种子保持绿。

**退出标准：** `LegacyTurnEngine` 与当前 `ChatService` 行为逐字一致；50 轮回放全绿；没有生产行为变化。

**注意：** 这是"提取"，不是"重写"。先让旧代码在新接口后面跑通，再换引擎。

#### P2：MAF 引擎（第 4–6 周，15 人日）

**目标：** `MafAgentEngine` 达到与 `LegacyTurnEngine` 行为等价（读路径优先）。

**做法：**

- 实现 `Agent.Harness.Maf`：`ChatModelChatClient`、`JsonSchemaAIFunction`、`MafAgentEngine`、`MafEventMapper`、`UsageCollector`、`GovernanceMiddleware`；
- 工具先接**只读**的约 20 个；写工具（`apply_record_action`、配置写）暂时禁用；
- 开关：`Assistant:Engine=legacy|maf`（部署配置，不是业务参数）；
- 灰度：内部账号 → 5% 用户 → 25%；只读工具；
- 每轮比对：最终文本、工具调用序列、usage、SSE 事件、落库消息；
- 收集 p50/p95 延迟、token、成本、错误率。

**退出标准：** 回放 parity 100%；线上灰度抽样 ≥ 99%（允许模型措辞差异，不允许工具调用/权限/成本差异）；无 P1/P2 缺陷；延迟增幅 ≤ 10%。

#### P3：写路径与默认切换（第 7–8 周，10 人日）

**目标：** 写工具接入，100% 用户切到 MAF；旧引擎保留回滚。

**做法：**

- 接通 `apply_record_action` / `preview_record_action` / 配置写工具；
- 用 `CallId` 透传幂等键；写路径契约测试；
- 高风险工具评估 `ApprovalRequiredAIFunction`（可选，先不做，避免改变 UX）；
- 灰度 50% → 100%；
- 观察 2 周：错误率、成本、延迟、用户反馈；
- 稳定后删除 `LegacyTurnEngine`（保留 git 历史）。

**退出标准：** 写路径 0 数据错误、0 幂等冲突；越权 0 泄露；成本增幅 ≤ 5%；2 周无 P1/P2；一键回滚演练通过。

#### P4：抽包与第二宿主（第 9–11 周，15 人日）

**目标：** 把 EOS 私有实现抽成 `Agent Harness`，证明可复用。

**做法：**

- 建独立解决方案（独立 git 仓库，不放进 EOS）；
- 把 `Abstractions/Core/Maf/Testing` 抽出去；EOS 改为项目引用或本地 NuGet 源；
- 目标框架 `net8.0;net10.0`；
- 写 `README` + `quickstart` + 3 个样例：console、ASP.NET Core、EOS 风格工具；
- 第二宿主：一个非 ERP 的小工具（文档问答/合同审阅/内部知识库），用 5–10 个工具跑通；
- 发布 `0.1.0-preview` 到本地/私有 NuGet。

**退出标准：** 第二宿主 < 150 行完成接入；EOS 不再引用 `Agent Harness` 内部实现；包能独立构建/测试。

**注意：** 如果第二宿主不存在，不要抽包；先保持 EOS 内部分层，等第二个真实消费者出现。**过早抽象比不抽象更贵。**

#### P5：工作流试点（第 12 周起，3–4 周/场景）

**目标：** 用 MAF Workflows 做一个确定性多步场景，验证"Agent + Workflow"组合。

**候选场景（按价值排序）：**

1. **月末结账检查**：收集库存/账务证据 → 逐项校验 → 生成差异清单 → 人工确认 → 生成报告；
2. **配置变更审批**：读现状 → 生成变更集 → 预演 → 人工逐项勾选 → 应用 → 发布 → 回读验证；
3. **单据卡住诊断与修复**：诊断 → 证据链 → 建议动作 → 人工确认 → 执行 → 回读。

**做法：**

- `WorkflowBuilder` + 4–6 个 `Executor`；
- 语言推理节点用 `ChatClientAgent`，确定性节点用纯 C#；
- 人工介入用 `RequestInfoExecutor` 或工具审批；
- 检查点先用内存；只有"跨请求恢复"真实需要时，才写 SQL `CheckpointManager`；
- 前端复用现有操作卡/请求卡，不新造 UI 范式。

**退出标准：** 场景端到端跑通；人工确认点可审计；失败可重试；不引入新基础设施。

**不要做：** Durable Task、Azure Functions、Foundry Hosted、A2A。除非部署形态变成 Azure。

### 6.3 决策门（Go / No-Go）

| 门 | 时点 | 通过条件 | 不通过 |
|---|---|---|---|
| G0 | P0 结束 | 10/10 用例；Schema / 推理 / 收尾三项无阻断 | 停止迁移，保留自研 |
| G1 | P1 结束 | 50 轮回放 100%；现有测试全绿 | 不进入 P2 |
| G2 | P2 结束 | 读路径回放 parity 100%；线上抽样 ≥ 99%；延迟 ≤ +10% | 延长灰度 / 修适配 |
| G3 | P3 结束 | 写路径 0 数据错误；成本 ≤ +5%；2 周无 P1/P2 | 回滚 legacy |
| G4 | P4 结束 | 第二宿主 < 150 行接入；包可独立构建 | 不抽包，保持 EOS 内分层 |

---

## 7. EOS 文件级改造清单

### 7.1 新增

| 文件 | 作用 |
|---|---|
| `EOS.API/Features/Assistant/Engine/IAssistantTurnEngine.cs` | 引擎接口（EOS 侧） |
| `EOS.API/Features/Assistant/Engine/LegacyTurnEngine.cs` | 旧循环原样搬迁（P1） |
| `EOS.API/Features/Assistant/Engine/MafAgentEngine.cs` | MAF 引擎适配（P2） |
| `EOS.API/Features/Assistant/Engine/TurnContext.cs` | 用户/会话/快照/页面/预算 |
| `EOS.API/Features/Assistant/Engine/TurnTranscript.cs` | 录制/回放 |
| `EOS.API.Tests/AssistantEngineParityTests.cs` | parity 测试 |
| `Agent Harness/`（独立仓库） | 可复用包（P4） |

### 7.2 修改

| 文件 | 改动 | 风险 |
|---|---|---|
| `ChatService.cs` | 保留前置/后置；循环委托给 `IAssistantTurnEngine` | 中；需逐段搬迁 |
| `Program.cs` | 注册 `IAssistantTurnEngine`，按开关选 `Legacy`/`Maf` | 低 |
| `EOS.API.csproj` | 增加 `Agent Harness` 项目引用（P2 起） | 低 |
| `appsettings.json` | 增加 `Assistant:Engine`（部署开关） | 低；不写业务配置 |
| `docs/guide/60-AI工作助手.md` | 增加"引擎分层"一节 | 低 |
| `docs/status.md` | 迁移完成后更新 §4.2 | 低 |

### 7.3 不修改

- `EOS.Web`：SSE 契约不变，前端零改动；
- `ASSISTANT_*` 表：第一版零迁移；
- 31 个工具的实现：只加适配器，不改业务逻辑；
- 权限/审计/写管线：不动；
- `IChatModel` 的公开契约：不动（只加适配器）。

### 7.4 开关与回滚

```json
// appsettings.json（部署配置，非业务参数）
"Assistant": {
  "Engine": "legacy"   // legacy | maf
}
```

- 回滚：改回 `legacy` + 重启（或如果做了热刷新，则无需重启）；旧引擎保留至少 2 周；
- 灰度：按用户/账号白名单，不按模块（模块维度会让同一用户看到两种行为）；
- 监控：`agent.engine` 标签区分新旧；对比错误率、延迟、成本；
- 删除：2 周稳定后删除 `LegacyTurnEngine`，但保留 git 历史与 ADR。

---

## 8. 验收与度量

### 8.1 功能 parity 清单

| # | 项 | 验收 |
|---|---|---|
| 1 | 31 个工具的名称/描述/Schema | 与迁移前逐字一致（快照比对） |
| 2 | 多轮工具调用上限 | 与旧引擎一致；最后一轮不带工具 |
| 3 | 只读工具并行 | 第一版可串行；如恢复并行，必须只读且不重名 |
| 4 | 工具调用标记清理 | 正文不出现 DSML/标记块 |
| 5 | `reasoning` 事件 | 透传、不落库、不回喂 |
| 6 | usage/成本 | 逐轮累加；回放对比 ≤ 1%，线上 ≤ 5% |
| 7 | 取消/断流 | 半截回复不落库；预算释放 |
| 8 | 错误分类 | 401/402/429/413/5xx 文案一致 |
| 9 | 会话隔离 | 跨用户 404；SQL 隔离 |
| 10 | 审计 | `ACTOR_TYPE=Agent`；工具调用可追溯 |
| 11 | 记忆/处境/提示词 | 注入顺序与内容一致 |
| 12 | 写工具 | 预演/执行/幂等/审计一致 |
| 13 | 配置写 | 变更集/预演/应用一致 |
| 14 | 批核请求卡 | 只准备请求，不执行 |
| 15 | 知识库/指标 | 工具行为一致 |

### 8.2 安全验收

- `AssistantEval/authz`：0 泄露（硬门槛）；
- 新增越权用例：工具被管理员关闭后，模型即使"幻觉"调用也被拒；
- 工具 Schema 不接受客户端传入；
- 日志/消息不含密钥、Cookie、完整 prompt；
- 写路径幂等：同一 `CallId` 重放不产生第二笔；
- 提示注入：页面处境/工具结果作为数据注入，不改变系统指令；
- 审计：每次 AI 调用与工具执行都有 `correlationId`。

### 8.3 性能/成本验收

| 指标 | 基线 | 目标 |
|---|---|---|
| 首 token 延迟 p95 | 迁移前 2 周均值 | ≤ +10% |
| 整轮延迟 p95 | 同上 | ≤ +15%（工具串行可能增加） |
| 每轮 token | 同上 | ≤ +5% |
| 每轮成本 | 同上 | ≤ +5% |
| 错误率 | 同上 | 不高于基线 |
| 工具调用成功率 | 同上 | 不低于基线 |

### 8.4 切换/回滚标准

**继续放量：**

- 连续 3 天：错误率 ≤ 基线、成本 ≤ +5%、无 P1/P2；
- 抽样 30 轮人工复核：回答质量不低于旧引擎；
- 写路径 0 数据错误。

**立即回滚：**

- 任一越权/数据错误；
- 成本/延迟超阈值 20%；
- 工具调用序列与旧引擎系统性不一致；
- 用户投诉集中（同一问题 ≥ 3 例）。

---

## 9. 不做什么 / 反模式

1. **不要全量重写。** 先换循环，再换工作流；不要一次改 31 个工具。
2. **不要让 MAF 管身份/权限。** 工具内部重新授权是底线。
3. **不要把 `AgentSession` 落进 EOS 库。** DB 是唯一事实源。
4. **不要用 Harness 的生产能力。** Shell/文件/Web 搜索对 ERP 是风险面。
5. **不要用 Hosting/Durable/A2A 做本地部署。** 它们是云/长流程方案，不是当前形态。
6. **不要为了并行而并行。** `AllowConcurrentInvocation=true` 会让写工具并发；第一版关掉。
7. **不要用反射生成工具 Schema。** 现有 Schema 是产品契约，必须原样保留。
8. **不要让模型生成幂等键。** 从 `CallId` 推导，服务端注入。
9. **不要把 MAF 类型泄漏到控制器/领域层。** 防腐层是这次迁移的核心资产。
10. **不要在没有第二宿主前抽 6 个包。** 先 EOS 内分层，第二个消费者出现再抽。
11. **不要用真实模型做 parity。** 用录制回放。
12. **不要在没有评估基线时切换。** 先补 `AssistantEngineParityTests`。

---

## 10. ADR 草稿（建议新增）

```markdown
# ADR-041：工作助手引擎采用 Microsoft Agent Framework（分阶段）

- 状态：提议（待用户拍板）
- 日期：2026-10-23

## 背景
工作助手已稳定运行，但 Agent 循环为 EOS 私有实现，跨项目复用成本高。
Microsoft Agent Framework 1.x 已 GA 并支持 .NET 10 / IChatClient / 中间件 / 工作流。

## 决策
1. 采用 MAF 作为 Agent 执行引擎，但不采用其 Hosting/Durable/A2A/Harness。
2. EOS 数据库仍是会话、消息、用量、记忆的唯一事实源；不持久化 AgentSession。
3. 保留 IChatModel / IAssistantTool / SSE 契约；MAF 通过适配层接入。
4. 先读路径灰度，再写路径；旧引擎保留 2 周回滚窗口。
5. 在 EOS 稳定后，抽出 Agent Harness 作为跨项目复用层；第二个宿主出现前不拆包。

## 后果
- 正面：标准引擎、跨项目复用、工作流能力、生态/招人/交接更容易。
- 负面：新增依赖与学习成本；MAF 版本节奏快；需要 parity/评估投入。
- 风险：工具 Schema/调用身份/轮次语义差异；用适配器 + 契约测试 + 灰度控制。

## 复审条件
- MAF 出现破坏性升级；
- EOS 需要跨进程长流程（此时再评估 SQL checkpoint / Durable）；
- 第二个宿主出现（此时抽包）；
- 迁移后成本/延迟持续超阈值。
```

---

## 附录 A：MAF 关键 API 速查（.NET）

| 类型/成员 | 用途 | 注意 |
|---|---|---|
| `AIAgent` | Agent 抽象 | 统一 run/stream 接口 |
| `ChatClientAgent` | 基于 `IChatClient` 的 Agent | 单 Agent 首选 |
| `ChatClientAgentOptions` | Id/Name/Instructions/Tools/HistoryProvider | `UseProvidedChatClientAsIs` 可避免二次装饰 |
| `ChatOptions` | Instructions/Tools/Temperature/MaxOutputTokens | 每请求可覆盖 |
| `AIFunction` / `AIFunctionDeclaration` | 工具声明与执行 | 自定义子类可保留原 Schema |
| `AIFunctionFactory` | 从方法生成工具 | 会反射生成 Schema，慎用 |
| `ApprovalRequiredAIFunction` | 工具执行前人工审批 | 返回 `ToolApprovalRequestContent` |
| `FunctionInvokingChatClient` | 自动执行工具循环 | `MaximumIterationsPerRequest` / `AllowConcurrentInvocation` |
| `AgentSession` | 会话状态 | EOS 不持久化 |
| `ChatHistoryProvider` | 历史提供者 | 可接 EOS DB，但第一版不必 |
| `AgentResponse` / `AgentResponseUpdate` | 非流式/流式响应 | 内容模型：Text/FunctionCall/FunctionResult/Usage/Reasoning |
| `WorkflowBuilder` / `Executor` | 图工作流 | 确定性多步 |
| `CheckpointManager` | 检查点 | .NET 内置内存；SQL 需自定义 |
| `InProcessExecution` | 进程内运行工作流 | 适合 IIS 单进程 |

> API 以 MAF 1.25 为准；升级前先跑 spike 的 10 条用例。

## 附录 B：EOS 助手 → Agent Harness 映射

| EOS 组件 | Agent Harness 对应 | 处理 |
|---|---|---|
| `ChatService.StreamReplyAsync` 循环 | `MafAgentEngine` | 替换 |
| `IChatModel` / `OpenAiCompatibleChatModel` | `ChatModelChatClient` | 适配，不换传输 |
| `AssistantToolRegistry` | `IToolCatalog` | 保留，只读 |
| `IAssistantTool` | `ITool` + `JsonSchemaAIFunction` | 适配 |
| `AssistantToolRisk` | `ToolRisk` | 映射 |
| `ToolExecutionResult` | `ToolResult` | 映射 |
| `AssistantActionGate` | `IToolPolicy` | 保留，包一层 |
| `ASSISTANT_USAGE_DAY` | `ITurnBudget` / `IUsageLedger` | 保留 |
| `FailureBreaker` | `IBreaker` | 保留 |
| `WorkbenchAuditWriter` | `IAuditSink` | 保留 |
| `ToolCallTextGuard` | `StreamingTextSanitizer` | 保留 |
| `ChatStreamEvent` | `AgentEvent` | 映射 |
| `PageContext` | `AgentPageContext` | 保留 |
| `AssistantMemoryStore` | `IContextProvider` | 保留 |
| `AssistantSituationService` | `IContextProvider` | 保留 |
| `AssistantEval` | `Agent.Harness.Testing` + parity | 扩展 |

## 附录 C：参考链接

- [Microsoft Agent Framework Overview](https://learn.microsoft.com/en-us/agent-framework/overview/agent-framework-overview)
- [Agent concepts](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/)
- [Running agents](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/running-agents)
- [Agent middleware](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/middleware/)
- [Session](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/session)
- [Tools overview](https://learn.microsoft.com/en-us/agent-framework/agents/tools/)
- [Function tools](https://learn.microsoft.com/en-us/agent-framework/agents/tools/function-tools)
- [Tool approval](https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval)
- [Workflows](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/)
- [Workflow checkpoints](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints)
- [Self-hosting](https://learn.microsoft.com/en-us/agent-framework/hosting/self-hosting/)
- [Model providers](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/)
- [MAF at BUILD 2026（1.0 GA 2026-04-02）](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-at-build-2026-announce/)
- [MAF GitHub](https://github.com/microsoft/agent-framework)
- [Durable Extension](https://github.com/microsoft/agent-framework-durable-extension)
- [NuGet: Microsoft.Agents.AI](https://www.nuget.org/packages/Microsoft.Agents.AI)

---

## 附：给"下一步"的三句话

1. **先做 P0 spike，不要先改 EOS。** 用 5 天验证 10 条用例；过不了就停在适配层，不要为了框架改工具 Schema。
2. **先建 parity，再换引擎。** 把旧循环原样搬到 `LegacyTurnEngine`，录制 50 轮；没有等价性证据，不要灰度。
3. **先 EOS 一个宿主，再抽 Agent Harness。** 第二个真实宿主出现前，所有"通用抽象"都只是猜测。
