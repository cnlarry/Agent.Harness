# P0 Spike 报告：MAF 1.25 + DeepSeek

- 日期：2026-10-23
- 环境：Windows / .NET SDK 10.0.402 / `Microsoft.Agents.AI` 1.25.0 / `Microsoft.Extensions.AI` 10.11.0
- 模型：`deepseek-flash`，`https://api.deepseek.com/chat/completions`
- 密钥：`DEEPSEEK_APP_KEY`（User 级环境变量；未写入仓库、日志或报告）
- 工程：[spikes/Agent.Harness.Spike](../../spikes/Agent.Harness.Spike/Agent.Harness.Spike.csproj)

## 结论

**10/10 通过。G0 建议“有条件通过”。**

Schema、推理、usage、取消、单/多工具、多轮在真实 `deepseek-flash` 上成立；最大轮次能收敛，但还没形成稳定的“未完成”语义。进入 P1 前必须先补 `AgentEvent.Failed` 和最大轮次的宿主契约，否则线上会把“空回复 + tool_calls”当成成功。

## 1. 方法

- 5 组真实模型用例（底层至少 10 次模型调用）覆盖单轮文本、单工具、3 轮工具、一轮双工具、取消；
- 工具 Schema 断言复用第 2 条的真实请求体，逐项 `JsonNode.DeepEquals`；
- 错误映射用本地 HTTP 桩（真实供应商的 402/413/429 不可控）；
- 最大轮次用脚本模型（可以稳定触发，不靠模型配合）；
- 每个用例都有明确断言，并记录事件序列、工具调用数、usage 与耗时。

## 2. 结果

| # | 用例 | 方式 | 结果 | 证据 |
|---|---|---|---|---|
| 01 | 单轮文本 | 真实 | ✅ | 1320 ms，`text="P0-TEXT-OK"` |
| 02 | 单工具调用 | 真实 | ✅ | 2313 ms，1 次调用，`CallId` 非空，最终回复含“已发货” |
| 03 | 多轮工具调用（3 轮） | 真实 | ✅ | 3262 ms，`SO-1001 → SO-1002 → SO-1003`，第 3 次 start 前已有 2 次 finish |
| 04 | 一轮多个工具调用 | 真实 | ✅ | 2021 ms，2 次调用，2 次 start 都在第一次 finish 之前 |
| 05 | 工具 Schema 原样保留 | 真实请求体 | ✅ | 3/3 个 schema 与 `ToolDescriptor.ParametersJson` 语义相等 |
| 06 | `reasoning_content` 透传 | 真实 | ✅ | 239 个 reasoning 字符进入 `ReasoningDelta`，最终文本不含推理内容 |
| 07 | usage 统计 | 真实 | ✅ | `prompt=41 / completion=72 / total=113`，`Total = Prompt + Completion` |
| 08 | 取消/断流 | 真实 | ✅ | 585 ms 内取消，抛 `OperationCanceledException`，无 `Completed` |
| 09 | 401/402/429/413/5xx 错误映射 | 本地桩 | ✅ | 5 个状态码都映射到稳定错误码，异常消息不含桩密钥 |
| 10 | 最大轮次收尾 | 脚本模型 | ✅ | 4 ms，`MaxToolRounds=2` → 3 次模型调用、2 次工具执行、3 次 start / 3 次 finish、`finish=max_tool_rounds`、空文本 |

> 第 10 条“通过”指事件收敛、没有挂起、没有未配对事件；`FinishReason=max_tool_rounds` 明确表示未完成，宿主不能把空文本展示为成功。

## 3. P0 期间发现并修复的问题

### 3.1 一轮多个 tool call 触发 DeepSeek 400

首轮两个工具调用时，`ChatModelChatClient` 把每个 tool call 作为独立的 `ChatResponseUpdate` 发出；MAF 聚合成了多条 assistant 消息，供应商看到第一条 assistant 的 `tool_calls` 后面没有对应的 `tool` 消息，返回：

```text
An assistant message with 'tool_calls' must be followed by tool messages
responding to each 'tool_call_id'.
```

修复：

- 同一次模型调用的所有增量共享 `MessageId`；
- 一轮的多个 tool call 合并到同一条 update（[ChatModelChatClient.cs](../../src/Agent.Harness.Maf/ChatModelChatClient.cs#L43-L129)）。

### 3.2 多个 tool result 被合并，丢失 `tool_call_id`

`FunctionInvokingChatClient` 可能把两个 `FunctionResultContent` 放进同一条 MEAI 消息；原实现只保留最后一个 `ToolCallId`，导致 DeepSeek 仍然报“tool 消息不足”。

修复：按 `FunctionResultContent` 拆成多条 `role=tool` 消息，一条一个 `tool_call_id`（[ChatModelChatClient.cs](../../src/Agent.Harness.Maf/ChatModelChatClient.cs#L179-L230)）。

### 3.3 `FinishReason` 取错且没有传给 MAF

原实现先遇到 `tool_calls` 就锁定 `FinishReason`，最终返回 `tool_calls` 而不是 `stop`；同时 `ChatResponseUpdate.FinishReason` 从未赋值。修复：

- 取最后一次 finish reason（[MafAgentEngine.cs](../../src/Agent.Harness.Maf/MafAgentEngine.cs#L75-L78)）；
- 映射 `stop/length/tool_calls/content_filter` 并写入 update（[ChatModelChatClient.cs](../../src/Agent.Harness.Maf/ChatModelChatClient.cs#L167-L175)）。

### 3.4 最大轮次留下只有 start、没有 finish 的工具调用

`MaximumIterationsPerRequest` 结束后，供应商还会返回一个新的 tool call；MAF 不执行它，但引擎已经发了 `ToolStarted`。修复：对未执行的 pending call 补一条 `Ok=false` 的 `ToolFinished`，摘要为“未执行：达到最大工具轮次”，写入 `AgentTurnResult.ToolCalls`，并把 `FinishReason` 改为 `max_tool_rounds`（[MafAgentEngine.cs](../../src/Agent.Harness.Maf/MafAgentEngine.cs#L143-L160)）。

### 3.5 工具拒绝被当成成功，`Draft` 被丢弃

`ToolFinished.Ok` 原来只看 `FunctionResultContent.Exception`；策略返回 `ToolResult.Deny` 时异常为空，事件仍然 `Ok=true`。`ToolResult.Draft` 也没有进入 `AgentTurnResult.Drafts`。

修复：按 `CallId` 暂存 `ToolResult`，结果事件使用 `ToolResult.Ok`，并把 `Draft` 收集到结果（[MafAgentEngine.cs](../../src/Agent.Harness.Maf/MafAgentEngine.cs#L109-L124)、[MafAgentEngine.cs](../../src/Agent.Harness.Maf/MafAgentEngine.cs#L171-L221)）。

## 4. P0 之后的 P1 前置修复

### 4.1 已完成

1. **`AgentEvent.Failed` 有了生产者。** 新增 `ChatModelException`（稳定 `Code`、安全 `Message`、`IsTransient`）；`TurnPipeline` 把引擎异常转成 `Failed`，取消仍向上抛。SSE 现在会收到 `error` 事件，不再无事件断流。
2. **最大轮次有稳定标记。** `MafAgentEngine` 在未执行 tool call 时设置 `FinishReason=max_tool_rounds`；`TurnPipeline` 按未完成结算与审计，不会记成成功。
3. **HTTP 入口不再信任请求体。** `MapAgentHarness` 只绑定 `Input`/`ConversationId`/`PageContext`，身份取 Claims，系统提示/轮次取服务端 `AgentHarnessEndpointOptions`，并改走 `TurnPipeline`。默认 `ClaimsAgentTurnFactory` 可替换。

### 4.2 仍待完成

1. **Provider 只是 spike 实现。** `DeepSeekChatModel` 没有重试/退避、代理、限流、token 预算、密钥轮换钩子；不要直接搬进生产。
2. **HTTP 线路上 Schema 是语义相等。** `IChatModel` 边界已做逐字快照（`ParametersJson` 与 `ToolDescriptor` 完全一致）；但 DeepSeek 适配器会重新序列化，HTTP JSON 的空白/属性顺序会被规范化。
3. **大工具结果只保留 200 字符摘要。** 完整结果回喂模型，`ToolCallRecord` 只存摘要；这是现有契约，不是 P0 回归。
4. **Conversation 归属仍由宿主校验。** `ClaimsAgentTurnFactory` 只校验非空，不校验“这个用户是否拥有这个 conversationId”；宿主应在 `IConversationStore` 或自定义 factory 中做。
5. **`max_tool_rounds` 不是 `Failed` 事件。** 它是 `AgentTurnResult.FinishReason`；前端/宿主必须显式处理，不能只看 HTTP 200 或 `Completed`。

### 4.3 P1 回放基线

- `Agent.Harness.Testing` 新增 `ChatCallTranscript` / `TurnTranscript`、`RecordingChatModel`、`ReplayChatModel`、`TurnRun` / `TurnRunComparer`。
- 回放会逐次校验模型收到的消息、tool call 参数与工具 Schema；请求漂移直接失败，不会出现“回放绿但输入已变”。
- 50 条构造 transcript 全部通过：文本、单/多工具、多轮、拒绝/审批、草稿、大结果、最大轮次。
- **这是构造场景，不是 EOS 真机录制**；EOS `LegacyTurnEngine` 与真实 transcript 的旧/新对比仍未做。

### 4.4 契约与安全补强

- 工具 Schema 在 `IChatModel` 边界逐字快照：三套 schema 的 `ParametersJson` 与 `ToolDescriptor` 完全一致。
- 越权用例：策略拒绝不执行工具且不泄露敏感值；工具按 `TenantId`/`UserId` 重新授权；非 owner 调用返回拒绝且不泄露数据。
- 端点测试捕获最终 `AgentTurnRequest`，确认请求体里的 `TenantId`/`UserId` 被忽略，身份/提示词/轮次来自 Claims 与服务端选项。
- 会话归属：`IConversationStore.IsOwnedByAsync` 校验租户/用户/会话；跨用户端点返回 403 且不执行引擎；`InMemoryConversationStore` 按三者隔离消息。
- SSE 失败端到端：引擎抛 `ChatModelException` 时端点发出 `event: error`、没有 `event: done`，预算/审计按未完成结算。
- 适配器契约：`ChatModelChatClient` 的 reasoning、usage、取消传播与 `ChatModelException` 透传都有独立测试。
- 非 `ChatModelException` 的异常只映射为 `agent_error` 泛化消息，不把原始异常文本带进事件。

### 4.5 读路径开关

- `MafAgentEngineOptions.ReadOnlyToolsOnly`：只暴露 `ReadOnly=true` 且 `Risk=Read` 的工具；`AddAgentHarnessMaf` 通过 DI 注册时默认开启。
- `EnabledTools`：工具名 allowlist；空数组表示一个都不暴露（fail-closed），不会退回“全部”。
- `AllowConcurrentToolInvocation` 默认 false；并发探针测试验证同一轮两个工具在 false 下串行、true 下并行。
- `AddAgentHarnessMaf(IConfiguration)` 从配置节绑定 options；options 是启动时快照，切换需要重启/重新部署。
- 直接 `new MafAgentEngine` 保持旧行为（全部工具、串行），避免破坏测试/spike；生产接线走 DI 默认值。

## 5. 接入事实（MAF 1.25 / DeepSeek）

- DeepSeek 的 tool call 以分片形式流式返回：首片带 `id`/`name`，后续片只带 `arguments`；`finish_reason=tool_calls` 与 `usage` 出现在最后一个 chunk。
- `reasoning_content` 是独立字段，`deepseek-flash` 默认就会产出；必须映射到 `TextReasoningContent`，不能拼进正文。
- `usage` 需要请求 `stream_options.include_usage=true`；最终 chunk 里才有 `prompt_tokens`/`completion_tokens`。
- `FunctionInvokingChatClient.MaximumIterationsPerRequest = N` 的实测语义是 **N 轮工具执行、最多 N+1 次模型调用**；不要在文档里写成“N 次模型调用”。
- `FunctionInvokingChatClient.CurrentContext.CallContent.CallId` 在回调中可用，`CallId` 非空，可作幂等键。
- `ChatResponseUpdate.MessageId` 是把同一次模型调用的增量聚合为一条消息的关键；不设它会拆消息。
- `ChatResponseUpdate.FinishReason` 可写，类型为 `ChatFinishReason`，构造函数接受原始字符串。

## 6. 依赖与体积

| 项 | 值 |
|---|---|
| `Agent.Harness.Maf.dll`（net10.0 Debug） | 38.4 KB |
| `Agent.Harness.Spike.dll` | 73.7 KB |
| `Microsoft.Agents.AI` nupkg | 2.15 MB |
| 顶级包 | `Microsoft.Agents.AI` 1.25.0、`Microsoft.Extensions.AI` 10.11.0、`Microsoft.Extensions.Configuration.Binder` 10.0.13、`Microsoft.Extensions.Logging.Abstractions` 10.0.13 |

值得注意的传递依赖：`Google.Protobuf` 3.30.2、`Microsoft.ML.Tokenizers` 2.0.0、`Microsoft.Extensions.AI.Evaluation` 10.10.0、`Microsoft.Extensions.VectorData.Abstractions` 10.10.0。Harness 的代码很小，但 MAF 的依赖面明显更大；P2 灰度前要把这些传递依赖纳入部署/漏洞扫描。

## 7. 复现

```powershell
$env:DEEPSEEK_APP_KEY = [Environment]::GetEnvironmentVariable('DEEPSEEK_APP_KEY','User')
dotnet run --project spikes\Agent.Harness.Spike\Agent.Harness.Spike.csproj

# 只跑本地桩
dotnet run --project spikes\Agent.Harness.Spike\Agent.Harness.Spike.csproj -- --stub-only

# 只跑某条用例
dotnet run --project spikes\Agent.Harness.Spike\Agent.Harness.Spike.csproj -- --case 04
```

结果写入 `artifacts/spike/p0-results.json`（已被 `.gitignore` 忽略）。

## 8. G0 建议

**有条件通过。** 条件 1–3 已在 P1 前置中完成，剩第 4 条：

1. ✅ 补 `AgentEvent.Failed` / 异常映射，SSE 不再无 `error` 事件断流；
2. ✅ 定义最大轮次语义：`FinishReason=max_tool_rounds`，`TurnPipeline` 按未完成结算；
3. ✅ 修 `MapAgentHarness` 的身份与 `TurnPipeline` 绕过问题；
4. 🟡 回放 harness 与 50 条构造 transcript 已落；EOS `LegacyTurnEngine` / 真实 transcript 对比待接入。

P0 的 10 条用例不替代 EOS 读路径 parity；`MafAgentEngine` 仍不能称为生产可用。
