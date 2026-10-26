# 架构

> 目标：把“引擎”和“治理壳”分开。引擎可换，治理壳复用，业务留在宿主。

## 1. 分层

```text
宿主（ASP.NET Core / Console / Job）
   │ 组装系统提示、历史、页面处境、当前用户
   ▼
Agent.Harness.Core
   TurnPipeline：预算预留 → IAgentEngine → 结算 + 审计
   ToolRegistry：白名单、风险分级、按名解析
   AgentEvent：稳定事件流
   │
   ▼
Agent.Harness.Maf
   MafAgentEngine：ChatClientAgent + FunctionInvokingChatClient
   ChatModelChatClient：IChatModel → IChatClient
   JsonSchemaAIFunction：保留原工具 Schema
   │
   ▼
MAF 核心 / 模型供应商 / 宿主工具 / 宿主权限与写管线
```

## 2. 依赖矩阵

| 项目 | Abstractions | Core | Maf | AspNetCore | Testing | MAF |
|---|---|---|---|---|---|---|
| Abstractions | — | — | — | — | — | — |
| Core | ✅ | — | — | — | — | — |
| Maf | ✅ | ✅ | — | — | — | ✅ |
| AspNetCore | ✅ | ✅ | — | — | — | — |
| Testing | ✅ | ✅ | — | — | — | — |
| 宿主 | ✅ | ✅ | ✅ | ✅ | 测试用 | 只在 Maf 内 |

**不允许反向依赖。** `Abstractions` 不知道 Core，Core 不知道 MAF，Maf 不知道 ASP.NET Core。

## 3. 一次回合

```text
1. 宿主：认证 → 取当前用户 → 组装 AgentTurnRequest
   （ASP.NET Core：MapAgentHarness → AgentChatRequest → IAgentTurnFactory）
2. TurnPipeline：budget.ReserveAsync
3. TurnPipeline：audit.WriteAsync("agent.turn.started")
4. IAgentEngine.StreamAsync：
     - 发工具声明（服务端 Schema）
     - 调模型
     - 解析 tool calls
     - 每个 tool：IToolPolicy.EvaluateAsync → ITool.InvokeAsync
     - 回喂 tool result → 继续，直到无 tool calls 或达到 MaxToolRounds
     - 产出 TextDelta / ToolStarted / ToolFinished / Completed
5. TurnPipeline：引擎异常 → Failed；budget.SettleAsync + audit.WriteAsync("agent.turn.finished")
6. 宿主：落库、SSE 下发、前端渲染
```

**HTTP 入口：** `MapAgentHarness` 只接受 `Input` / `ConversationId` / `PageContext`；默认 `ClaimsAgentTurnFactory` 从 Claims 取 `TenantId`/`UserId`、从服务端选项取 `SystemPrompt`/`MaxToolRounds`/`HistoryLimit`，再交给 `TurnPipeline`。客户端不能提供身份、系统提示或轮次上限。

**失败与收尾：** 引擎抛 `ChatModelException` 时，`TurnPipeline` 产出 `Failed(Code, Message)` 并按未完成结算；其他异常映射为 `agent_error`；取消仍向上抛。达到最大轮次时产出 `Completed`，但 `FinishReason=max_tool_rounds`，宿主必须按未完成处理。

**落库位置：** 会话/消息/用量/审计都在宿主；Harness 不持有自己的 Session Store。

## 4. 引擎契约

```csharp
public interface IAgentEngine
{
    IAsyncEnumerable<AgentEvent> StreamAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken = default);
}
```

引擎只负责“跑完一轮并产出事件”。它不知道：

- 用户有没有权限；
- 消息怎么落库；
- 预算怎么扣；
- 前端怎么渲染。

`MafAgentEngine` 会在 `IChatModel` 外包一层 `EmptyResponseRetryChatModel`：只对“无正文、无 tool call、`finish_reason=stop`/null”的响应重试，默认 1 次；`length`、`tool_calls`、异常不重试。

这些都在宿主/管线。

## 5. 工具契约

```csharp
public interface ITool
{
    ToolDescriptor Descriptor { get; }
    ValueTask<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default);
}
```

- `Descriptor.ParametersJson` 是服务端权威 Schema；
- `ToolInvocation` 带 `TenantId` / `UserId` / `ConversationId` / `CallId`；
- 工具内部必须按 `UserId` 重新授权；
- 失败用 `ToolResult.Deny`，不要把业务异常抛进对话流；
- 引擎级选择由 `MafAgentEngineOptions` 控制：`ReadOnlyToolsOnly`（DI 默认 true）、`EnabledTools` allowlist（空数组 fail-closed）、`AllowConcurrentToolInvocation`（默认 false）。直接构造 `MafAgentEngine` 保持“全部工具、串行”以兼容测试/spike。

## 6. 事件契约

`AgentEvent` 与 SSE 六种事件同构：

| 事件 | 含义 |
|---|---|
| `TextDelta` | 正文增量 |
| `ReasoningDelta` | 推理内容增量（不落库、不回喂） |
| `ToolStarted` | 开始执行工具 |
| `ToolFinished` | 工具返回摘要 |
| `Completed` | 回复完成（含最终结果与 usage） |
| `Failed` | 失败（稳定错误码 + 用户文案） |

宿主可映射为 SSE、SignalR、控制台；`Agent.Harness.AspNetCore` 提供最小 SSE 映射。

## 7. 数据与状态

| 数据 | 位置 | 说明 |
|---|---|---|
| 会话/消息 | 宿主数据库 | 唯一事实源；`IConversationStore.IsOwnedByAsync` 校验租户/用户归属，跨用户 403 |
| 用量/预算 | 宿主台账 | `ITurnBudget` 实现 |
| 审计 | 宿主审计表 | `IAuditSink` 实现 |
| 工具调用摘要 | 宿主消息表 | `AgentTurnResult.ToolCalls` |
| MAF `AgentSession` | 不持久化 | 单请求内可选；不做双写 |
| 工作流检查点 | 阶段 5 再定 | 先用内存；需要跨进程再写 SQL |

## 8. 明确不在本层

- 认证/授权/数据范围；
- 统一表单写管线/幂等/审批卡；
- 向量库/RAG/记忆；
- 多 Agent 编排；
- Hosting / Durable / A2A / Harness；
- UI。

这些要么在宿主，要么在后续阶段单独引入。

## 9. 测试与回放

`Agent.Harness.Testing` 在 `IChatModel` 边界提供录制/回放：

- `RecordingChatModel` 包装真实/脚本模型，记录每次调用的消息、工具声明与流式输出；
- `ReplayChatModel` 逐次校验消息、tool call 参数与工具 Schema，再回放录制增量；请求漂移直接抛 `TranscriptMismatchException`；
- `TurnRun` / `TurnRunComparer` 比较两个回合的事件、文本、工具调用、usage 与结束原因。

当前有 50 条构造 transcript（文本、单/多工具、多轮、拒绝/审批、草稿、大结果、最大轮次），全部 record → replay 通过。它们不访问真实模型；EOS `LegacyTurnEngine` / 真实 transcript 的旧/新对比仍待接入。Transcript 目前是内存对象，接 EOS 时再补 JSON 持久化。
