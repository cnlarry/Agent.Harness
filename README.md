# Agent Harness

> 企业管理系统里的**受控 Agent 运行时**：把模型、工具、权限、预算、审计和写入管线装进一个可复用的壳。
>
> 状态：`0.1.0` 骨架 · `dotnet build` 0 警告 0 错误 · `dotnet test` 106/106 通过 · P0 spike 10/10（`deepseek-flash`） · 回放基线 50/50 · EOS 已作为唯一引擎接入

---

## 命名说明（先读）

本项目叫 **Agent Harness**，但**不是** Microsoft Agent Framework 里的 `HarnessAgent`。

- 这里的 **Harness** 取“约束/装具”之意：给 Agent 套上权限、审计、预算、工具白名单和写管线；
- MAF 的 `HarnessAgent` 是一个自带 Shell、文件访问、Web 搜索、Todo 的“开箱即用长任务 Agent”；
- 两者不是同一层东西。本项目**不采用** MAF Harness，只采用 MAF 的 Agent/工具/工作流核心。

命名空间与包统一为 `Agent.Harness.*`。如果未来与生态里的 “Agent Harness” 产生混淆，README 与包描述必须继续显式区分。

---

## 为什么做这个

EOS 的工作助手已经证明了一件事：**企业系统接入 LLM，难点不在“跑一个 Agent 循环”，而在“让这个循环在权限、审计和写管线里合法地跑”。**

EOS 当时自研了 `ChatService` + `IChatModel` + 31 个受控工具，跑得很好，但它是 EOS 私有的：

- 换一个企业管理系统，工具、权限、会话、审计都要重写一遍；
- 想用 MAF 的标准引擎，又不想把 ERP 的权限/写管线交给框架；
- 想要工作流、检查点、人工介入，但不想引入 Azure/Durable 基础设施。

Agent Harness 的目标就是补这一层：

```text
MAF / 其他引擎  ｜  权限、审计、预算、工具治理、会话、SSE  ｜  各业务系统
     引擎层      ｜              Agent Harness              ｜   宿主
```

**引擎可换，治理壳复用，业务留在宿主。**

---

## 它是什么 / 不是什么

| 是 | 不是 |
|---|---|
| 企业管理系统里的 Agent 治理壳 | 通用多 Agent 框架 |
| MAF 引擎的适配层 + 稳定契约 | MAF Hosting / Durable / Harness 的封装 |
| 工具白名单、策略、预算、审计、SSE 的复用件 | 权限系统、写管线、审批流的替代品 |
| 单 Agent + 工具循环 + 可选工作流 | Agent 市场、Agent 编排平台 |
| 可嵌入 ASP.NET Core / 控制台 / 后台任务 | 自带 UI 的产品 |

**一句话：它不替你决定“能不能做”，只负责“怎么把已经授权的动作跑完并留下证据”。**

---

## 架构

```text
宿主（ASP.NET Core / 控制台 / 后台任务）
   │  组装：系统提示、历史、页面处境、当前用户
   ▼
Agent.Harness.Core
   ├─ TurnPipeline：预算预留 → 引擎执行 → 结算 + 审计
   ├─ ToolRegistry：白名单、风险分级、按名解析
   └─ AgentEvent：与 SSE 同构的稳定事件流
   │
   ▼
Agent.Harness.Maf
   ├─ MafAgentEngine：ChatClientAgent + FunctionInvokingChatClient
   ├─ ChatModelChatClient：IChatModel → IChatClient（防腐层）
   └─ JsonSchemaAIFunction：原样保留工具 JSON Schema
   │
   ▼
MAF 核心 / 现有模型供应商 / 宿主工具
```

### 依赖规则

| 项目 | 允许依赖 | 禁止 |
|---|---|---|
| `Agent.Harness.Abstractions` | 无 | 任何 Agent 框架、ASP.NET Core |
| `Agent.Harness.Core` | Abstractions | MAF、ASP.NET Core |
| `Agent.Harness.Maf` | Abstractions、Core、MAF 核心 | ASP.NET Core、业务系统 |
| `Agent.Harness.AspNetCore` | Abstractions、Core、ASP.NET Core | MAF |
| `Agent.Harness.Testing` | Abstractions、Core | 真实网络/数据库 |
| 宿主 | 以上全部 | 把 MAF 类型泄漏到控制器/领域层 |

**硬规则：`AIAgent` / `ChatMessage` / `AgentSession` 等 MAF 类型只允许出现在 `Agent.Harness.Maf`。**

---

## 当前状态

| 项目 | 内容 | 状态 |
|---|---|---|
| `Agent.Harness.Abstractions` | 回合、事件、工具、治理、会话、模型契约 | ✅ 可编译 |
| `Agent.Harness.Core` | `ToolRegistry`、`TurnPipeline`、DI 扩展 | ✅ 可编译 |
| `Agent.Harness.Maf` | `ChatModelChatClient`、`JsonSchemaAIFunction`、`MafAgentEngine` | ✅ 可编译；P0 spike 10/10（`deepseek-flash`）；EOS 已作为唯一引擎接入 |
| `Agent.Harness.AspNetCore` | `MapAgentHarness` SSE 端点、Claims 组装、`TurnPipeline` 接线 | ✅ 最小安全实现 |
| `Agent.Harness.Testing` | `FakeChatModel`、内存预算/审计、`EchoAgentEngine`、录制/回放 | ✅ 可编译 |
| `tests/Agent.Harness.Tests` | 工具注册表、回合管线、Fake 模型、MAF 适配、端点组装、Schema 快照、越权、会话归属、SSE 失败、50 条回放、读路径选项、收尾轮说明 | ✅ 106/106 |
| `samples/Agent.Harness.Sample.Console` | 无模型依赖的回显示例 | ✅ 可运行 |
| `spikes/Agent.Harness.Spike` | P0：MAF + DeepSeek + 3 工具；P2：100 次读路径基线（不打包） | ✅ 10/10；文本 p50 749 / 工具 1717 ms |

已实测：

```text
dotnet build Agent.Harness.slnx   → 0 警告 0 错误
dotnet test                        → 106/106 通过
dotnet run --project samples/...   → delta + done
dotnet run --project spikes/...    → P0 10/10（需 DEEPSEEK_APP_KEY）
```

> ⚠️ P0 spike 已在 `deepseek-flash` 上通过 10/10，覆盖单/多工具、多轮、usage、取消、错误映射与最大轮次；它只是独立 spike 的读路径验证。EOS 已把 `MafAgentEngine` 作为唯一引擎接入（权限、写管线、预算、审计仍在宿主机），第二个宿主接入与抽包未开始。

---

## 技术栈

| 组件 | 版本 | 说明 |
|---|---|---|
| .NET SDK | 10.0.100+（本机 10.0.402） | `global.json` 用 `latestFeature` 滚动 |
| 目标框架 | 库 `net8.0;net10.0`；测试/示例 `net10.0` | 兼容 LTS 与当前 |
| `Microsoft.Agents.AI` | `1.25.0` | 只用核心 Agent/工具/工作流，不引 Hosting/Durable |
| `Microsoft.Extensions.AI` | `10.11.0` | `IChatClient` / 内容模型 |
| `Microsoft.Extensions.*` | `10.0.13` | DI / Logging |
| xUnit | `2.9.3` | 单元测试 |

版本集中在 [Directory.Packages.props](Directory.Packages.props)；升级前先跑 P0 的 10 条用例。

---

## 快速开始

要求：.NET SDK 10.0.100+（本机 10.0.402）。

```powershell
dotnet build Agent.Harness.slnx
dotnet test tests\Agent.Harness.Tests\Agent.Harness.Tests.csproj
dotnet run --project samples\Agent.Harness.Sample.Console\Agent.Harness.Sample.Console.csproj
dotnet pack Agent.Harness.slnx -c Release -o artifacts\packages
```

示例输出：

```text
delta: Echo: 你好，Agent Harness
done: Echo: 你好，Agent Harness (0 tokens)
```

`dotnet pack` 会生成 `0.1.0` 的五个 nupkg（Abstractions/Core/Maf/AspNetCore/Testing）到 `artifacts/packages`；测试与示例不打包。

示例故意不接真实模型：它验证的是契约、管线、事件与宿主接线。接真实模型是下一步。

---

## 仓库结构

```text
Agent Harness/
├─ Agent.Harness.slnx
├─ Directory.Build.props          # 统一 TFM / nullable / warnings-as-errors
├─ Directory.Packages.props       # 中央包版本
├─ global.json                    # SDK 10.0.100+，latestFeature
├─ src/
│  ├─ Agent.Harness.Abstractions/ # 稳定契约，无框架依赖
│  ├─ Agent.Harness.Core/         # 管线 + 工具注册表
│  ├─ Agent.Harness.Maf/          # MAF 适配层（唯一允许引用 MAF 的项目）
│  ├─ Agent.Harness.AspNetCore/   # SSE 端点
│  └─ Agent.Harness.Testing/      # 测试替身 + 录制/回放
├─ tests/Agent.Harness.Tests/
├─ samples/Agent.Harness.Sample.Console/
├─ spikes/Agent.Harness.Spike/      # P0：MAF + DeepSeek 验证（不打包）
└─ docs/
   ├─ blueprint.md                # MAF 可行性 + 完整蓝图（主文档）
   ├─ architecture.md             # 分层与依赖规则
   ├─ roadmap.md                  # P0–P5
   ├─ secrets.md                  # 模型密钥配置与轮换
   ├─ spikes/
   │  ├─ p0-spike-report.md       # P0 10 条用例与结论
   │  └─ p2-baseline-report.md    # P2 读路径 100 次基线
   └─ decisions/
      ├─ ADR-001-name-and-scope.md
      ├─ ADR-002-http-endpoint-trust-boundary.md
      ├─ ADR-003-conversation-ownership.md
      ├─ ADR-004-read-path-tool-selection.md
      └─ ADR-005-empty-response-retry.md
```

---

## 核心概念

| 类型 | 作用 | 谁实现 |
|---|---|---|
| `IAgentEngine` | 跑完一轮对话，产出 `AgentEvent` | `MafAgentEngine` / 测试替身 / 未来其他引擎 |
| `IChatModel` | 供应商无关的最小模型流 | 宿主（DeepSeek/Kimi/GLM/Qwen/OpenAI 兼容） |
| `ITool` | 一个受控工具；内部必须重新授权 | 宿主 |
| `IToolPolicy` | 执行前判定允许/拒绝/需审批 | 宿主 |
| `ITurnBudget` | 预留与结算额度，防并发超卖 | 宿主 |
| `IAuditSink` | 审计事件落点 | 宿主 |
| `IConversationStore` | 会话/消息持久化契约 + `IsOwnedByAsync` 归属校验 | 宿主 |
| `ToolRegistry` | 白名单、风险分级、按名解析 | Core |
| `TurnPipeline` | 预算 + 引擎 + 结算 + 审计 | Core |

---

## 加一个工具

```csharp
using Agent.Harness.Abstractions;

public sealed class SearchOrdersTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new(
        Name: "search_orders",
        Description: "按关键字查询订单（只读）",
        ParametersJson:
        """
        {
          "type": "object",
          "properties": {
            "keyword": { "type": "string", "description": "客户名或单号关键字" }
          },
          "required": ["keyword"]
        }
        """,
        Risk: ToolRisk.Read,
        ReadOnly: true,
        AllowParallel: true);

    public async ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        // 1. 按 invocation.UserId 重新授权（不要信任模型或前端）
        // 2. 用既有查询/仓储取数
        // 3. 只回喂紧凑文本；结构化卡片放 Draft
        await Task.Yield();
        var keyword = invocation.Arguments.GetProperty("keyword").GetString();
        return ToolResult.Success($"query={keyword}");
    }
}
```

注册：

```csharp
services.AddScoped<ITool, SearchOrdersTool>();
services.AddAgentHarnessCore();
```

**两条硬约束：**

1. 工具 Schema 由服务端硬编码，绝不接受客户端传入；
2. 工具执行时按当前用户重新授权，`IToolPolicy` 只是执行前门禁，不是权限依据。

---

## 接入 MAF 引擎

```csharp
using Agent.Harness.Abstractions;
using Agent.Harness.AspNetCore;
using Agent.Harness.Core;
using Agent.Harness.Maf;

builder.Services.AddScoped<IChatModel, MyChatModel>();
builder.Services.AddScoped<IToolPolicy, MyToolPolicy>();
builder.Services.AddScoped<ITurnBudget, MyTurnBudget>();
builder.Services.AddScoped<IAuditSink, MyAuditSink>();
builder.Services.AddScoped<ITool, SearchOrdersTool>();

builder.Services.AddAgentHarnessCore();
builder.Services.AddAgentHarnessMaf(options =>
{
    // 读路径灰度默认只暴露 Read + ReadOnly 工具；写路径阶段再显式关闭。
    options.ReadOnlyToolsOnly = true;
    options.EnabledTools = ["search_orders"]; // 可选 allowlist；空数组表示一个都不暴露
    options.AllowConcurrentToolInvocation = false;
    options.MaxEmptyResponseRetries = 1; // 空正文/仅 reasoning 的安全重试
});
builder.Services.AddAgentHarnessAspNetCore(options =>
{
    options.SystemPrompt = "你是企业管理系统里的助手。";
    options.TenantClaimType = "tenant_id";
    options.MaxToolRounds = 4;
});
```

灰度开关也可以放在配置里（部署时切换，重启生效）：

```json
{
  "AgentHarness": {
    "Maf": {
      "ReadOnlyToolsOnly": true,
      "EnabledTools": ["search_orders"],
      "AllowConcurrentToolInvocation": false,
      "MaxEmptyResponseRetries": 1
    }
  }
}
```

```csharp
builder.Services.AddAgentHarnessMaf(
    builder.Configuration.GetSection("AgentHarness:Maf"));
```

ASP.NET Core SSE：

```csharp
using Agent.Harness.AspNetCore;

app.MapAgentHarness("/api/agent/chat")
   .RequireAuthorization();
```

**注意：**

- `MapAgentHarness` 不做认证/授权；必须在端点外层加 `RequireAuthorization` 或等价策略；
- 请求体只接受 `Input` / `ConversationId` / `PageContext`；`TenantId` / `UserId` 来自 Claims，`SystemPrompt` / `MaxToolRounds` 来自 `AddAgentHarnessAspNetCore` 的服务端选项，客户端无法伪造；
- 默认 `ClaimsAgentTurnFactory` 从 Claims 取身份、从 `IConversationStore` 取历史，并用 `IsOwnedByAsync` 校验会话归属；跨用户返回 403；
- 端点走 `TurnPipeline`，预算/审计不会被绕过；引擎异常会转为 SSE `error` 事件（`AgentEvent.Failed`）；
- `AddAgentHarnessMaf` 通过 DI 注册时默认 `ReadOnlyToolsOnly=true`；直接 `new MafAgentEngine` 默认保留全部工具（兼容测试/spike）。写路径阶段显式关闭只读，不要靠“没配就是全开”；
- `EnabledTools` 是 allowlist；空数组表示一个都不暴露，不会退回“全部工具”；
- `MaxEmptyResponseRetries` 默认 1，只对“无正文、无工具调用、stop/null”重试；`length`、`tool_calls`、异常不重试，0 表示关闭；
- 灰度开关可通过 `IConfiguration` 绑定；当前 options 是启动时单例，切换需要重启/重新部署，回滚演练仍待做；
- `MaximumIterationsPerRequest = MaxToolRounds` 的 P0 实测口径是 **N 轮工具 + N+1 次模型调用**；最后一次若仍请求工具，结果 `FinishReason=max_tool_rounds`，宿主不能当正常完成；
- `AllowConcurrentToolInvocation` 默认 false；测试已验证同一轮两个工具会串行执行。不要为了延迟直接打开写工具的并发；
- 工具幂等键从 `FunctionInvokingChatClient.CurrentContext?.CallContent.CallId` 取，不从模型参数取。

---

## 设计约束

1. **MAF 是引擎，不是架构。** 领域代码不出现 MAF 类型。
2. **数据库是会话唯一事实源。** 不持久化 `AgentSession`，不做双写。
3. **工具每次执行重新授权。** `IToolPolicy` 不是权限边界。
4. **模型输出永不进入 SQL/表达式解析器。**
5. **工具 Schema 服务端所有。**
6. **预算与审计不是可选项。** 管线默认包住。
7. **失败以 `ToolResult.Ok=false` 回喂模型，不向对话流抛业务异常。**
8. **不引入 Hosting / Durable / Harness / A2A**，除非部署形态真的变成 Azure/多机长流程。
9. **单元测试不访问真实模型/数据库。** 用 `FakeChatModel` 与录制回放。
10. **第二个宿主出现前不过度拆包。** 先让 EOS 跑通，再抽通用件。

---

## 路线图

| 阶段 | 内容 | 状态 |
|---|---|---|
| **P0** | 独立 spike：MAF 1.25 + DeepSeek + 3 工具，10 条用例 | ✅ 10/10（2026-10-23；见 [报告](docs/spikes/p0-spike-report.md)） |
| **P1** | 契约稳定 + 录制回放 + 引擎对比 | ✅ 回放基线 50/50；EOS 旧引擎对比已完成（合成 2 + 真实重建 10，全 PASS），随后旧引擎退役、对拍工具一并退役 |
| **P2** | MAF 读路径灰度（只读工具） | ✅ EOS 已切为唯一引擎；只读过滤期演练里写意图降级为说明、不执行 |
| **P3** | 写路径接入 + 默认切换 + 回滚 | 🟡 EOS 写工具已接同一引擎，幂等键来自引擎 `CallId`；运行数据待积累 |
| **P4** | 抽包 + 第二宿主验证 | ⬜ 未开始 |
| **P5** | MAF Workflows 试点（确定性多步 + 人工确认） | ⬜ 未开始 |

详细路线、验收标准、文件级改造清单见 **[docs/blueprint.md](docs/blueprint.md)**。

---

## 测试与评估

```powershell
dotnet test tests\Agent.Harness.Tests\Agent.Harness.Tests.csproj
```

当前覆盖：

- `ToolRegistry` 的排序、命中、未知工具；
- `TurnPipeline` 的预算预留/结算、审计起止；
- `FakeChatModel` 的预设回合；
- `TurnPipeline` 的失败事件、取消/结算语义；
- `ChatModelChatClient` 的多 tool call 分组、多 tool result 拆分、`FinishReason` 传递、reasoning/usage、取消与 `ChatModelException` 透传；
- `MafAgentEngine` 的完整工具循环：FakeChatModel 派发 tool call → 工具执行 → 回喂 → 最终文本；
- `MafAgentEngine` 的一轮多工具、拒绝 `Ok=false`、`Draft` 透传、最大轮次收尾；
- `MafAgentEngineOptions`：只读过滤、allowlist（空数组 fail-closed）、DI 默认只读、`IConfiguration` 绑定、并发开关串行/并行、空正文重试；
- `MapAgentHarness` 的 Claims 组装与 `TurnPipeline` 接线；`ClaimsAgentTurnFactory` 的身份/历史加载；
- SSE 失败端到端：引擎异常 → `event: error`，无 `event: done`，预算/审计按未完成结算；
- 会话归属：`InMemoryConversationStore` 按租户/用户/会话隔离；跨用户读取被拒绝，端点返回 403 且不执行引擎；
- `RecordingChatModel` / `ReplayChatModel` / `TurnRunComparer`：在 `IChatModel` 边界录制、严格匹配请求后回放；
- 50 条构造 transcript 的 record → replay 对比（文本、单/多工具、多轮、拒绝、草稿、大结果、最大轮次）；
- 工具 Schema 逐字快照：`IChatModel` 边界的 `ParametersJson` 与 `ToolDescriptor` 完全一致；
- 越权/泄露用例：策略拒绝不执行工具、工具按请求身份重新授权、拒绝结果不含敏感值、非 `ChatModelException` 不泄露原消息；
- P0 spike 的真实模型 10 条用例（手动运行，不进入 `dotnet test`）；
- P2 baseline runner：100 次真实调用，文本/只读工具分开统计 p50/p95、token、工具调用率、空正文与重试。

下一步要补：

- 宿主侧写路径的运行数据（EOS 已接入写工具，幂等键只从引擎 `CallId` 推导）；
- baseline 扩到 500 并接供应商价格；统计空正文重试率；
- HTTP 线路上 Schema 仍是语义等价（JSON 序列化会规范化空白/顺序），`IChatModel` 边界才是逐字快照。

**引擎对比不要用真实模型**：同一 prompt 两次输出不同，分不清是引擎差异还是模型抖动；回归用录制回放。

---

## 安全

- 认证/授权由宿主负责；`MapAgentHarness` 只是端点，默认从 Claims 组装身份，不读请求体里的租户/用户；
- 会话归属由 `IConversationStore.IsOwnedByAsync` 校验；跨用户访问返回 403，不能只靠“历史为空”；
- 端点走 `TurnPipeline`；引擎异常转为 `AgentEvent.Failed`，不会无 `error` 事件断流；
- 读路径灰度默认只暴露只读工具；`EnabledTools` 空数组 fail-closed，写路径必须显式关闭 `ReadOnlyToolsOnly`；
- 同一轮工具默认串行（`AllowConcurrentToolInvocation=false`），不要为延迟直接打开写工具并发；
- 最大轮次以 `FinishReason=max_tool_rounds` 表示未完成，宿主不能当成功展示；
- 工具内部必须重新授权，不能只信 `IToolPolicy`；
- 密钥只放环境变量/密钥库，不进数据库、不进日志；配置与轮换见 [docs/secrets.md](docs/secrets.md)；
- 审计记录 `TenantId` / `UserId` / `ConversationId` / `CorrelationId`；
- 页面处境、工具结果都是**数据**，不能当指令拼接；
- 高风险写动作应使用结构化确认（MAF `ApprovalRequiredAIFunction` 或宿主自己的操作卡），不要靠自然语言“好的”；
- 当前 `IToolPolicy.RequireApproval` 在 `MafAgentEngine` 里是 **fail-closed**（拒绝并回喂原因），结构化审批在 P3 接入。

---

## 文档

- **[docs/blueprint.md](docs/blueprint.md)**：为什么当时没选 MAF、MAF 现状与边界、可行性评分、Agent Harness 的完整蓝图、P0–P5、验收与 ADR 草稿。
- **[docs/secrets.md](docs/secrets.md)**：模型密钥的环境变量/密钥库配置、轮换与泄漏处置。
- **[docs/spikes/p0-spike-report.md](docs/spikes/p0-spike-report.md)**：P0 的 10 条真实/桩用例、修复记录与 G0 结论。
- **[docs/spikes/p2-baseline-report.md](docs/spikes/p2-baseline-report.md)**：P2 读路径 100 次真实调用的 p50/p95、token、空正文与重试基线。
- **[docs/architecture.md](docs/architecture.md)**：分层与依赖规则。
- **[docs/roadmap.md](docs/roadmap.md)**：阶段与决策门。
- **[docs/decisions/ADR-001-name-and-scope.md](docs/decisions/ADR-001-name-and-scope.md)**：命名、范围与“不是 MAF Harness”的决策。
- **[docs/decisions/ADR-002-http-endpoint-trust-boundary.md](docs/decisions/ADR-002-http-endpoint-trust-boundary.md)**：HTTP 入口的身份、管线与失败/最大轮次语义。
- **[docs/decisions/ADR-003-conversation-ownership.md](docs/decisions/ADR-003-conversation-ownership.md)**：会话归属校验与 403 语义。
- **[docs/decisions/ADR-004-read-path-tool-selection.md](docs/decisions/ADR-004-read-path-tool-selection.md)**：读路径灰度、只读过滤与并发默认值。
- **[docs/decisions/ADR-005-empty-response-retry.md](docs/decisions/ADR-005-empty-response-retry.md)**：空正文/仅 reasoning 的重试边界。
- **[AGENTS.md](AGENTS.md)**：给后续开发会话的项目指引。

---

## License

[MIT](LICENSE) © 2026 Larry (github.com/cnlarry)。

引用本项目时请保留版权声明与许可声明。`samples/`、`tests/`、`spikes/` 与本库同一许可。
